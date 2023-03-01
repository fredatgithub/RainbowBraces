﻿using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using RainbowBraces.Tagger;
using Match = System.Text.RegularExpressions.Match;

namespace RainbowBraces
{
    public class RainbowTagger : ITagger<IClassificationTag>
    {
        private const int _maxLineLength = 100000;
        private const int _overflow = 200;

        private readonly ITextBuffer _buffer;
        private readonly List<ITextView> _views = new();
        private readonly IClassificationTypeRegistryService _registry;
        private readonly ITagAggregator<IClassificationTag> _aggregator;
        private readonly VerticalAdornmentsColorizer _verticalAdornmentsColorizer;
        private readonly AllowanceResolver _allowanceResolver;
        private readonly Debouncer _debouncer;
        private List<ITagSpan<IClassificationTag>> _tags = new();
        private readonly BracePairCache _pairsCache = new();
        private readonly List<IMappingTagSpan<IClassificationTag>> _tagList = new();
        private readonly List<(SnapshotSpan, TagAllowance)> _spanList = new();
        private bool _isEnabled;
        private bool _scanWholeFile;
        private int? _startPositionChange;
        private static readonly Regex _regex = new(@"[\{\}\(\)\[\]\<\>]", RegexOptions.Compiled);
        private static Regex _specializedRegex;

        public RainbowTagger(ITextView view, ITextBuffer buffer, IClassificationTypeRegistryService registry, ITagAggregator<IClassificationTag> aggregator, IClassificationFormatMapService formatMap)
        {
            _buffer = buffer;
            _registry = registry;
            _aggregator = aggregator;
            _verticalAdornmentsColorizer = new(formatMap);
            _allowanceResolver = GetAllowanceResolver(buffer);
            _isEnabled = IsEnabled(General.Instance);
            _scanWholeFile = General.Instance.VerticalAdornments;
            _debouncer = new(General.Instance.Timeout);

            _buffer.Changed += OnBufferChanged;
            General.Saved += OnSettingsSaved;
            AddView(view);

            if (_isEnabled)
            {
                ThreadHelper.JoinableTaskFactory.StartOnIdle(async () =>
                {
                    await ParseAsync();
                    HandleRatingPrompt();
                }, VsTaskRunContext.UIThreadIdlePriority).FireAndForget();
            }
        }

        public void AddView(ITextView view)
        {
            _verticalAdornmentsColorizer.RegisterViewAsync(view).FireAndForget();

            if (view.IsClosed) return;
            if (_views.Contains(view)) return;

            view.Closed += OnViewClosed;
            view.LayoutChanged += View_LayoutChanged;
            _views.Add(view);
        }

        private void RemoveView(ITextView view)
        {
            view.Closed -= OnViewClosed;
            view.LayoutChanged -= View_LayoutChanged;
            _views.Remove(view);
            if (_views.Count != 0) return;

            view.TextBuffer.Changed -= OnBufferChanged;
            General.Saved -= OnSettingsSaved;
        }

        private void View_LayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_scanWholeFile) return;
            if (_isEnabled && (e.VerticalTranslation || e.HorizontalTranslation))
            {
                _debouncer.Debouce(() => { _ = ParseAsync(); });
            }
        }

        private void OnSettingsSaved(General settings)
        {
            if (IsEnabled(settings))
            {
                _scanWholeFile = settings.VerticalAdornments;
                _debouncer.Debouce(() => { _ = ParseAsync(); });
            }
            else
            {
                _pairsCache.Clear();
                _tags.Clear();
                _specializedRegex = null;
                _scanWholeFile = false;
                int visibleStart = GetVisibleStart();
                int visibleEnd = GetVisibleEnd();
                TagsChanged?.Invoke(this, new(new(_buffer.CurrentSnapshot, visibleStart, visibleEnd - visibleStart)));
            }

            _isEnabled = IsEnabled(settings);
            _verticalAdornmentsColorizer.Enabled = settings.VerticalAdornments && _isEnabled;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            ITextView view = (ITextView)sender;
            RemoveView(view);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_isEnabled && e.Changes.Count > 0)
            {
                int startPosition = e.Changes.Min(change => change.OldPosition);

                // Save the topmost change to field. Debouncer can call ParseAsync with earlier parameter that can be wrong now.
                _startPositionChange = _startPositionChange == null
                    ? startPosition
                    : Math.Min(_startPositionChange.Value, startPosition);

                _debouncer.Debouce(() => { _ = ParseAsync(startPosition); });
            }
        }

        IEnumerable<ITagSpan<IClassificationTag>> ITagger<IClassificationTag>.GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (_tags.Count == 0 || spans.Count == 0 || spans[0].IsEmpty)
            {
                return null;
            }

            return _tags.Where(p => spans[0].IntersectsWith(p.Span.Span));
        }

        public async Task ParseAsync(int topPosition = 0)
        {
            // If we are parsing after change pick the topmost change that occured.
            if (topPosition != 0)
            {
                topPosition = _startPositionChange ?? topPosition;
                _startPositionChange = null;
            }

            General options = await General.GetLiveInstanceAsync();

            // Must be performed on the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_buffer.CurrentSnapshot.LineCount > _maxLineLength)
            {
                await VS.StatusBar.ShowMessageAsync($"No rainbow braces. File too big ({_buffer.CurrentSnapshot.LineCount} lines).");
                return;
            }

            int visibleStart = GetVisibleStart();
            int visibleEnd = GetVisibleEnd();
            ITextSnapshotLine changedLine = _buffer.CurrentSnapshot.GetLineFromPosition(topPosition);
            int changeStart = changedLine.Start.Position;

            SnapshotSpan wholeDocSpan = new(_buffer.CurrentSnapshot, 0, visibleEnd);

            // Add tags to instantiated list to reduce allocations on UI thread and increase responsiveness
            // We expect tags count not to differ a lot between invocations so memory should not be wasted a lot
            _tagList.Clear();
            _tagList.AddRange(_aggregator.GetTags(wholeDocSpan));

            // Move the rest of the execution to a background thread.
            await TaskScheduler.Default;

            // Filter tags and get their spans
            _spanList.Clear();
            _spanList.AddRange(_tagList
                .Select(tag => (Tag: tag, Allowance: _allowanceResolver.GetAllowance(tag)))
                .SelectMany(d => d.Tag.Span.GetSpans(_buffer).Where(s => !s.IsEmpty).Select(span => (span, d.Allowance))));
            IList<(SnapshotSpan Span, TagAllowance Allowance)> allDisallow = _spanList;

            // Create builders for each brace type
            BracePairBuilderCollection builders = new();
            if (options.Parentheses) builders.AddBuilder('(', ')');
            if (options.CurlyBrackets) builders.AddBuilder('{', '}');
            if (options.SquareBrackets) builders.AddBuilder('[', ']');
            if (options.AngleBrackets) builders.AddBuilder('<', '>', new[] { TagAllowance.Punctuation }); // Allow only punctuation

            // If not selected all braces use specialized regex for only selected ones
            Regex regex = options.Parentheses && options.CurlyBrackets && options.SquareBrackets && options.AngleBrackets
                ? _regex
                : _specializedRegex ??= BuildRegex(options);

            // Prepare structure of all disallowed tag spans for faster linear processing
            (Span Span, TagAllowance Allowance, int Index)[] indexedDisallow = allDisallow.Select((tuple, i) => (tuple.Span.Span, tuple.Allowance, i)).ToArray();
            (Span Span, TagAllowance Allowance, int Index)[] disallowFromStart = indexedDisallow.OrderBy(s => s.Span.Start).ToArray();
            (Span Span, TagAllowance Allowance, int Index)[] disallowFromEnd = indexedDisallow.OrderBy(d => d.Span.End).ToArray();
            int fromStartAdd = 0;
            int fromEndRemove = 0;
            Dictionary<int, (Span Span, TagAllowance Allowance)> possibleMatchingSpans = new();
            List<(Span Span, TagAllowance Allowance)> matchingSpans = new();

            if (changedLine.LineNumber > 0)
            {
                builders.LoadFromCache(_pairsCache, changeStart);
            }

            foreach (ITextSnapshotLine line in _buffer.CurrentSnapshot.Lines)
            {
                // Ignore ignore empty lines
                if (line.Extent.IsEmpty)
                {
                    continue;
                }

                int lineStart = line.Start;
                int lineEnd = line.End;

                // Remove all disallowed tags with end before this line
                while (true)
                {
                    if (fromEndRemove >= disallowFromEnd.Length) break;
                    (Span Span, TagAllowance Allowance, int Index) indexedSpan = disallowFromEnd[fromEndRemove];
                    if (indexedSpan.Span.End >= lineStart) break;
                    possibleMatchingSpans.Remove(indexedSpan.Index);
                    fromEndRemove++;
                }

                // Add all disallowed tags with start on this line
                while (true)
                {
                    if (fromStartAdd >= disallowFromStart.Length) break;
                    (Span Span, TagAllowance Allowance, int Index) indexedSpan = disallowFromStart[fromStartAdd];
                    if (indexedSpan.Span.Start > lineEnd) break;
                    possibleMatchingSpans.Add(indexedSpan.Index, (indexedSpan.Span, indexedSpan.Allowance));
                    fromStartAdd++;
                }

                // Ignore any line above change because it is already cached
                if ((changedLine.LineNumber > 0 && (lineEnd < changeStart)))
                {
                    continue;
                }

                // Scan line if it contains any braces
                string text = line.GetText();
                MatchCollection matches = regex.Matches(text);

                if (matches.Count == 0)
                {
                    continue;
                }

                foreach (Match match in matches)
                {
                    char c = match.Value[0];
                    int position = line.Start + match.Index;

                    // Enumeration of spans matching tags.
                    matchingSpans.Clear();
                    matchingSpans.AddRange(possibleMatchingSpans.Values.Where(s => s.Span.Start <= position && s.Span.End > position));

                    // If brace is part of another tag (not punctation, operator or delimiter) then ignore it. (eg. is in string literal)
                    if (matchingSpans.Any(s => s.Allowance == TagAllowance.Disallowed))
                    {
                        continue;
                    }

                    Span braceSpan = new(position, 1);

                    // Try all builders if any can accept matched brace
                    foreach (BracePairBuilder braceBuilder in builders)
                    {
                        if (braceBuilder.TryAdd(c, braceSpan, matchingSpans)) break;
                    }
                }

                if (line.End >= visibleEnd || line.LineNumber >= _buffer.CurrentSnapshot.LineCount)
                {
                    break;
                }
            }

            builders.SaveToCache(_pairsCache);
            _tags = GenerateTagSpans(builders.SelectMany(b => b.Pairs), options.CycleLength);
            TagsChanged?.Invoke(this, new(new(_buffer.CurrentSnapshot, visibleStart, visibleEnd - visibleStart)));
            if (options.VerticalAdornments) ColorizeVerticalAdornments();
        }

        private void HandleRatingPrompt()
        {
            if (_tags.Count > 0)
            {
                RatingPrompt prompt = new("MadsKristensen.RainbowBraces", Vsix.Name, General.Instance, 10);
                prompt.RegisterSuccessfulUsage();
            }
        }

        private List<ITagSpan<IClassificationTag>> GenerateTagSpans(IEnumerable<BracePair> pairs, int cycleLength)
        {
            List<ITagSpan<IClassificationTag>> tags = new();

            foreach (BracePair pair in pairs)
            {
                IClassificationType classification = _registry.GetClassificationType(ClassificationTypes.GetName(pair.Level, cycleLength));
                ClassificationTag openTag = new(classification);
                SnapshotSpan openSpan = new(_buffer.CurrentSnapshot, pair.Open);
                tags.Add(new TagSpan<IClassificationTag>(openSpan, openTag));

                ClassificationTag closeTag = new(classification);
                SnapshotSpan closeSpan = new(_buffer.CurrentSnapshot, pair.Close);
                tags.Add(new TagSpan<IClassificationTag>(closeSpan, closeTag));
            }

            return tags;
        }

        private int GetVisibleStart()
        {
            if (_scanWholeFile) return 0;

            int visibleStart = Math.Max(_views.Min(view => view.TextViewLines.First().Start.Position) - _overflow, 0);
            return visibleStart;
        }

        private int GetVisibleEnd()
        {
            if (_scanWholeFile) return _buffer.CurrentSnapshot.Length;

            int visibleEnd = Math.Min(_views.Max(view => view.TextViewLines.Last().End.Position) + _overflow, _buffer.CurrentSnapshot.Length);
            return visibleEnd;
        }

        /// <summary>
        /// Returns whether is enabled globally by settings and any brace is enabled or not.
        /// </summary>
        private static bool IsEnabled(General settings) => settings.Enabled && (settings.Parentheses || settings.CurlyBrackets || settings.SquareBrackets || settings.AngleBrackets);

        /// <summary>
        /// Create specialized regex for only partial of braces.
        /// </summary>
        private static Regex BuildRegex(General options)
        {
            StringBuilder pattern = new(10);
            pattern.Append('[');
            if (options.Parentheses) pattern.Append(@"\(\)");
            if (options.CurlyBrackets) pattern.Append(@"\{\}");
            if (options.SquareBrackets) pattern.Append(@"\[\]");
            if (options.AngleBrackets) pattern.Append(@"\<\>");
            pattern.Append(']');
            return new Regex(pattern.ToString(), RegexOptions.Compiled);
        }

        /// <summary>
        /// Feature to colorize vertical lines.
        /// It uses internals of Visual Studio. Might not work at all or break at any point.
        /// </summary>
        private void ColorizeVerticalAdornments()
        {
            _verticalAdornmentsColorizer.ColorizeVerticalAdornmentsAsync(_views.ToArray(), _tags).FireAndForget();
        }

        private static AllowanceResolver GetAllowanceResolver(ITextBuffer buffer)
        {
            string contentType = buffer.ContentType.TypeName.ToUpper();
            return contentType switch
            {
                ContentTypes.Css => new CssAllowanceResolver(),
                ContentTypes.Less => new CssAllowanceResolver(),
                ContentTypes.Scss => new CssAllowanceResolver(),
                _ => new DefaultAllowanceResolver()
            };
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;
    }
}
