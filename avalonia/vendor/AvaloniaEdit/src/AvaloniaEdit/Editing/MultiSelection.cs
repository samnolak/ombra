// Ombra's maintained AvaloniaEdit fork extension. Licensed under the MIT license in ../../../LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using AvaloniaEdit.Document;

namespace AvaloniaEdit.Editing
{
    /// <summary>Discontiguous selections using the editor's canonical input, read-only and undo pipeline.</summary>
    public sealed class MultiSelection : Selection
    {
        private readonly SelectionSegment[] _segments;
        private readonly int _primary;
        private readonly bool _backwards;

        public MultiSelection(TextArea textArea, IEnumerable<SelectionSegment> segments, int primaryOffset)
            : this(textArea, segments, primaryOffset, false) { }

        private MultiSelection(TextArea textArea, IEnumerable<SelectionSegment> segments, int primaryOffset, bool backwards)
            : base(textArea)
        {
            var sorted = segments.OrderBy(s => s.StartOffset).ThenBy(s => s.EndOffset).ToArray();
            var merged = new List<SelectionSegment>(sorted.Length);
            foreach (var segment in sorted)
            {
                if (segment.StartOffset < 0 || segment.EndOffset > textArea.Document.TextLength)
                    throw new ArgumentOutOfRangeException(nameof(segments));
                if (merged.Count > 0 && segment.StartOffset <= merged[merged.Count - 1].EndOffset)
                {
                    var previous = merged[merged.Count - 1];
                    merged[merged.Count - 1] = new SelectionSegment(previous.StartOffset, Math.Max(previous.EndOffset, segment.EndOffset));
                }
                else merged.Add(segment);
            }
            if (merged.Count == 0) throw new ArgumentException("At least one caret is required.", nameof(segments));
            _segments = merged.ToArray();
            _primary = Array.FindIndex(_segments, s => s.StartOffset <= primaryOffset && primaryOffset <= s.EndOffset);
            if (_primary < 0) _primary = _segments.Length - 1;
            _backwards = backwards;
        }

        public override IEnumerable<SelectionSegment> Segments => _segments;
        internal int CaretOffset(SelectionSegment segment) => _backwards ? segment.StartOffset : segment.EndOffset;
        public override ISegment SurroundingSegment => new SimpleSegment(_segments[0].StartOffset, _segments[_segments.Length - 1].EndOffset - _segments[0].StartOffset);
        public override int Length => _segments.Sum(s => s.Length);
        public override bool EnableVirtualSpace => false;
        public override TextViewPosition StartPosition => Position(_backwards ? _segments[_primary].EndOffset : _segments[_primary].StartOffset);
        public override TextViewPosition EndPosition => Position(_backwards ? _segments[_primary].StartOffset : _segments[_primary].EndOffset);
        public override string GetText() => string.Join(Environment.NewLine, _segments.Select(s => TextArea.Document.GetText(s)));
        private TextViewPosition Position(int offset) => new TextViewPosition(TextArea.Document.GetLocation(offset));
        public override bool Contains(int offset) => _segments.Any(s => s.StartOffset <= offset && offset <= s.EndOffset);

        public static void AddCaret(TextArea textArea, int offset)
        {
            var segments = textArea.Selection is MultiSelection multiple ? multiple._segments.ToList() : textArea.Selection.Segments.ToList();
            if (segments.Count == 0) segments.Add(new SelectionSegment(textArea.Caret.Offset, textArea.Caret.Offset));
            segments.Add(new SelectionSegment(offset, offset));
            textArea.Selection = new MultiSelection(textArea, segments, offset);
            textArea.Caret.Offset = offset;
        }

        public override void ReplaceSelectionWithText(string newText)
        {
            if (newText == null) throw new ArgumentNullException(nameof(newText));
            // All-or-nothing for protected spans; never partially apply a multi-caret gesture.
            foreach (var segment in _segments)
            {
                if (segment.Length == 0)
                {
                    if (!TextArea.ReadOnlySectionProvider.CanInsert(segment.StartOffset)) return;
                }
                else
                {
                    var deletable = TextArea.GetDeletableSegments(segment);
                    if (deletable.Length != 1 || deletable[0].Offset != segment.StartOffset || deletable[0].Length != segment.Length) return;
                }
            }
            var document = TextArea.Document;
            using (document.RunUpdate())
            {
                var carets = new TextAnchor[_segments.Length];
                for (var i = 0; i < carets.Length; i++)
                {
                    carets[i] = document.CreateAnchor(_segments[i].StartOffset);
                    carets[i].MovementType = AnchorMovementType.AfterInsertion;
                    carets[i].SurviveDeletion = true;
                }
                for (var i = _segments.Length - 1; i >= 0; i--)
                    document.Replace(_segments[i].StartOffset, _segments[i].Length, newText);
                TextArea.Selection = new MultiSelection(TextArea, carets.Select(a => new SelectionSegment(a.Offset, a.Offset)), carets[_primary].Offset);
                TextArea.Caret.Offset = carets[_primary].Offset;
            }
        }

        internal void Move(CaretMovementType direction, bool extend, bool delete = false)
        {
            var moved = new List<SelectionSegment>(_segments.Length);
            var primaryOffset = 0;
            var backwards = false;
            for (var i = 0; i < _segments.Length; i++)
            {
                var segment = _segments[i];
                var offset = _backwards ? segment.StartOffset : segment.EndOffset;
                if (delete && segment.Length != 0)
                {
                    moved.Add(segment);
                    if (i == _primary) primaryOffset = offset;
                    continue;
                }
                var position = Position(offset);
                var visualLine = TextArea.TextView.GetOrConstructVisualLine(TextArea.Document.GetLineByOffset(offset));
                position.VisualColumn = visualLine.GetVisualColumn(offset - visualLine.FirstDocumentLine.Offset);
                var desiredX = double.NaN;
                var next = CaretNavigationCommandHandler.GetNewCaretPosition(TextArea.TextView, position, direction, false, ref desiredX);
                var nextOffset = TextArea.Document.GetOffset(Math.Max(1, next.Line), Math.Max(1, next.Column));
                var anchor = extend || delete ? (_backwards ? segment.EndOffset : segment.StartOffset) : nextOffset;
                moved.Add(new SelectionSegment(anchor, nextOffset));
                if (i == _primary) { primaryOffset = nextOffset; backwards = nextOffset < anchor; }
            }
            var selection = new MultiSelection(TextArea, moved, primaryOffset, backwards);
            TextArea.Selection = selection;
            TextArea.Caret.Offset = primaryOffset;
            if (delete) selection.ReplaceSelectionWithText(string.Empty);
        }

        internal void InsertNewLine(string newline)
        {
            var document = TextArea.Document;
            using (document.RunUpdate())
            {
                ReplaceSelectionWithText(newline);
                if (ReferenceEquals(TextArea.Selection, this) || TextArea.Selection is not MultiSelection updated || TextArea.IndentationStrategy == null) return;
                var anchors = updated._segments.Select(s => document.CreateAnchor(s.EndOffset)).ToArray();
                foreach (var anchor in anchors)
                {
                    anchor.MovementType = AnchorMovementType.AfterInsertion;
                    anchor.SurviveDeletion = true;
                }
                foreach (var anchor in anchors.Reverse())
                {
                    var line = document.GetLineByOffset(anchor.Offset);
                    var deletable = TextArea.GetDeletableSegments(line);
                    if (deletable.Length == 1 && deletable[0].Offset == line.Offset && deletable[0].Length == line.Length)
                        TextArea.IndentationStrategy.IndentLine(document, line);
                }
                TextArea.Selection = new MultiSelection(TextArea, anchors.Select(a => new SelectionSegment(a.Offset, a.Offset)), anchors[updated._primary].Offset);
                TextArea.Caret.Offset = anchors[updated._primary].Offset;
            }
        }

        public override Selection UpdateOnDocumentChange(DocumentChangeEventArgs e)
        {
            var segments = _segments.Select(s =>
            {
                var start = e.GetNewOffset(s.StartOffset, AnchorMovementType.AfterInsertion);
                var end = s.Length == 0 ? start : Math.Max(start, e.GetNewOffset(s.EndOffset, AnchorMovementType.BeforeInsertion));
                return new SelectionSegment(start, end);
            });
            return new MultiSelection(TextArea, segments, e.GetNewOffset(_backwards ? _segments[_primary].StartOffset : _segments[_primary].EndOffset), _backwards);
        }

        public override Selection SetEndpoint(TextViewPosition endPosition) => Selection.Create(TextArea, StartPosition, endPosition);
        public override Selection StartSelectionOrSetEndpoint(TextViewPosition startPosition, TextViewPosition endPosition) => SetEndpoint(endPosition);
        public override bool Equals(object obj) => obj is MultiSelection other && TextArea == other.TextArea && _primary == other._primary && _backwards == other._backwards &&
            _segments.Length == other._segments.Length && _segments.Zip(other._segments, (a, b) => a.StartOffset == b.StartOffset && a.EndOffset == b.EndOffset).All(equal => equal);
        public override int GetHashCode() => HashCode.Combine(TextArea, _primary, _segments.Length, _backwards);
    }
}
