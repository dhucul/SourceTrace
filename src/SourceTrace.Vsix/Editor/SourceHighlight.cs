using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace SourceTrace.Editor
{
    internal sealed class HighlightLocation
    {
        internal HighlightLocation(string file, int line) { File = file; Line = line; }
        internal string File { get; }
        internal int Line { get; }
    }

    internal static class SourceHighlight
    {
        internal const string FormatName = "SourceTrace.TracedLine";
        private static HighlightLocation current;
        internal static HighlightLocation Current => Volatile.Read(ref current);
        internal static event EventHandler Changed;
#if SOURCETRACE_TEST_DIAGNOSTICS
        internal static int TaggersCreated, GetTagsCalls, TagsReturned;
        internal static string LastDocument;
#endif
        internal static void Set(string file, int line)
        {
            var next = string.IsNullOrWhiteSpace(file) || line <= 0 ? null : new HighlightLocation(file, line);
            Volatile.Write(ref current, next);
            var handlers = Changed;
            if (handlers == null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(null, EventArgs.Empty); }
                catch (Exception ex) { Trace.TraceError("SourceTrace marker notification: " + ex); }
            }
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name(SourceHighlight.FormatName)]
    [UserVisible(true)]
    internal sealed class TraceLineFormat : MarkerFormatDefinition
    {
        public TraceLineFormat()
        {
            DisplayName = "SourceTrace traced line";
            // A translucent fill preserves syntax colors in both dark and light themes.
            Fill = new SolidColorBrush(Color.FromArgb(60, 255, 191, 0));
            Fill.Freeze();
            Border = new Pen(Brushes.Goldenrod, 1.5);
            Border.Freeze();
            ZOrder = 2;
        }
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TagType(typeof(TextMarkerTag))]
    internal sealed class TraceLineTaggerProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents { get; set; }
        [Import] internal IEditorFormatMapService FormatMaps { get; set; }
        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            // Tag file-backed subject buffers too; the editor maps their spans into projection views.
            if (!typeof(T).IsAssignableFrom(typeof(TextMarkerTag)) || !Documents.TryGetTextDocument(buffer, out var document)) return null;
            if (view is IWpfTextView wpfView) TraceMarkerTheme.Attach(wpfView, FormatMaps);
            return new TraceLineTagger(view, buffer, document) as ITagger<T>;
        }
    }

    internal sealed class TraceLineTagger : ITagger<TextMarkerTag>, IDisposable
    {
        private readonly ITextView view;
        private readonly ITextBuffer buffer;
        private readonly ITextDocument document;
        private readonly object gate = new object();
        private ITrackingSpan marker;
        private HighlightLocation markerTarget;
        private bool disposed;

        internal TraceLineTagger(ITextView view, ITextBuffer buffer, ITextDocument document)
        {
#if SOURCETRACE_TEST_DIAGNOSTICS
            SourceHighlight.TaggersCreated++;
            SourceHighlight.LastDocument = document.FilePath;
#endif
            this.view = view; this.buffer = buffer; this.document = document;
            SourceHighlight.Changed += HighlightChanged;
            view.Closed += ViewClosed;
            buffer.Changed += BufferChanged;
            document.FileActionOccurred += FileActionOccurred;
            UpdateMarker();
        }
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
#if SOURCETRACE_TEST_DIAGNOSTICS
            SourceHighlight.GetTagsCalls++;
#endif
            if (spans.Count == 0) yield break;
            ITrackingSpan current;
            HighlightLocation owner;
            lock (gate) { if (disposed) yield break; current = marker; owner = markerTarget; }
            if (current == null || owner == null || !ReferenceEquals(owner, SourceHighlight.Current) ||
                !string.Equals(DocumentPath(), owner.File, StringComparison.OrdinalIgnoreCase)) yield break;
            var location = current.GetSpan(spans[0].Snapshot);
            foreach (var requested in spans)
            {
                if (!requested.IntersectsWith(location)) continue;
#if SOURCETRACE_TEST_DIAGNOSTICS
                SourceHighlight.TagsReturned++;
#endif
                yield return new TagSpan<TextMarkerTag>(location, new TextMarkerTag(SourceHighlight.FormatName));
                yield break;
            }
        }
        private void HighlightChanged(object sender, EventArgs args) { UpdateMarker(); Invalidate(); }
        private void BufferChanged(object sender, TextContentChangedEventArgs args) { Invalidate(); }
        private void FileActionOccurred(object sender, TextDocumentFileActionEventArgs args)
        {
            if ((args.FileActionType & (FileActionTypes.DocumentRenamed | FileActionTypes.ContentLoadedFromDisk)) == 0) return;
            UpdateMarker();
            Invalidate();
        }
        private void UpdateMarker()
        {
            var target = SourceHighlight.Current;
            var snapshot = buffer.CurrentSnapshot;
            ITrackingSpan next = null;
            if (target != null && string.Equals(DocumentPath(), target.File, StringComparison.OrdinalIgnoreCase) && target.Line <= snapshot.LineCount)
            {
                var line = snapshot.GetLineFromLineNumber(target.Line - 1);
                var span = line.Length == 0 ? line.ExtentIncludingLineBreak : line.Extent;
                next = snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeExclusive);
            }
            lock (gate)
            {
                // Span construction can outlive its target. Never overwrite a newer marker.
                if (!disposed && ReferenceEquals(target, SourceHighlight.Current))
                { marker = next; markerTarget = target; }
            }
        }
        private string DocumentPath()
        {
            try { return document.FilePath; }
            catch (ObjectDisposedException) { return null; }
        }
        private void Invalidate()
        {
            if (disposed) return;
            var snapshot = buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }
        private void ViewClosed(object sender, EventArgs args) { Dispose(); }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; marker = null; markerTarget = null; }
            SourceHighlight.Changed -= HighlightChanged;
            buffer.Changed -= BufferChanged;
            view.Closed -= ViewClosed;
            document.FileActionOccurred -= FileActionOccurred;
        }
    }
}
