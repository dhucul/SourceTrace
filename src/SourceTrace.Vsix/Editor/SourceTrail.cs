using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace SourceTrace.Editor
{
    internal sealed class TrailChange : EventArgs
    {
        internal TrailChange(string file, int line, long generation) { File = file; Line = line; Generation = generation; }
        internal string File { get; }
        internal int Line { get; }
        internal long Generation { get; }
    }

    // Recorded stops own the trail. Editor selection and filtering only own the current gold marker.
    internal static class SourceTrail
    {
        internal const string FormatName = "SourceTrace.VisitedLine";
        private static readonly object gate = new object();
        private static readonly Dictionary<string, HashSet<int>> files = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private static long generation;
        private static int count;
        internal static int Count { get { lock (gate) return count; } }
        internal static event EventHandler<TrailChange> Changed;

        internal static void Add(string file, int line)
        {
            if (string.IsNullOrWhiteSpace(file) || line <= 0) return;
            TrailChange change;
            lock (gate)
            {
                if (!files.TryGetValue(file, out var lines)) { lines = new HashSet<int>(); files.Add(file, lines); }
                if (!lines.Add(line)) return;
                count++;
                change = new TrailChange(file, line, generation);
            }
            Notify(change);
        }
        internal static void Clear()
        {
            TrailChange change;
            lock (gate)
            {
                if (count == 0) return;
                files.Clear(); count = 0;
                change = new TrailChange(null, 0, ++generation);
            }
            Notify(change);
        }
        internal static int[] Lines(string file, out long version)
        {
            lock (gate)
            {
                version = generation;
                return file != null && files.TryGetValue(file, out var lines) ? lines.ToArray() : Array.Empty<int>();
            }
        }
        internal static bool IsCurrent(long version) { lock (gate) return generation == version; }
        private static void Notify(TrailChange change)
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (EventHandler<TrailChange> handler in handlers.GetInvocationList())
            {
                // Clear can be reentered while another view is processing an earlier addition.
                if (!IsCurrent(change.Generation)) return;
                try { handler(null, change); }
                catch (Exception ex) { Trace.TraceError("SourceTrace trail notification: " + ex); }
            }
        }
    }

    [Export(typeof(EditorFormatDefinition)), Name(SourceTrail.FormatName), UserVisible(true)]
    internal sealed class VisitedLineFormat : MarkerFormatDefinition
    {
        public VisitedLineFormat()
        {
            DisplayName = "SourceTrace visited line";
            Fill = new SolidColorBrush(Color.FromArgb(65, 64, 190, 130)); Fill.Freeze();
            Border = new Pen(new SolidColorBrush(Color.FromArgb(150, 64, 190, 130)), 1); Border.Freeze();
            ZOrder = 1;
        }
    }

    [Export(typeof(IViewTaggerProvider)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document), TagType(typeof(TextMarkerTag))]
    internal sealed class TrailLineTaggerProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents { get; set; }
        [Import] internal IEditorFormatMapService FormatMaps { get; set; }
        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            if (!typeof(T).IsAssignableFrom(typeof(TextMarkerTag)) || !Documents.TryGetTextDocument(buffer, out var document)) return null;
            if (view is IWpfTextView wpfView) TraceMarkerTheme.Attach(wpfView, FormatMaps);
            return new TrailLineTagger(view, buffer, document) as ITagger<T>;
        }
    }

    internal sealed class TrailLineTagger : ITagger<TextMarkerTag>, IDisposable
    {
        private readonly ITextView view;
        private readonly ITextBuffer buffer;
        private readonly ITextDocument document;
        private readonly object gate = new object();
        private readonly Dictionary<int, ITrackingSpan> markers = new Dictionary<int, ITrackingSpan>();
        private string path;
        private long generation;
        private bool disposed;

        internal TrailLineTagger(ITextView view, ITextBuffer buffer, ITextDocument document)
        {
            this.view = view; this.buffer = buffer; this.document = document;
            SourceTrail.Changed += TrailChanged;
            SourceHighlight.Changed += CurrentChanged;
            view.Closed += ViewClosed;
            buffer.Changed += BufferChanged;
            document.FileActionOccurred += FileActionOccurred;
            Rebuild();
        }
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;
        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            KeyValuePair<int, ITrackingSpan>[] retained;
            string owner;
            long version;
            lock (gate)
            {
                if (disposed) yield break;
                owner = path; version = generation; retained = markers.ToArray();
            }
            if (!SourceTrail.IsCurrent(version) || !string.Equals(owner, DocumentPath(), StringComparison.OrdinalIgnoreCase)) yield break;
            var current = SourceHighlight.Current;
            foreach (var item in retained)
            {
                if (!SourceTrail.IsCurrent(version)) yield break;
                // Keep the current/selected line gold; all other visited lines remain green.
                if (current != null && current.Line == item.Key && string.Equals(current.File, owner, StringComparison.OrdinalIgnoreCase)) continue;
                var location = item.Value.GetSpan(spans[0].Snapshot);
                if (spans.Any(requested => requested.IntersectsWith(location)))
                    yield return new TagSpan<TextMarkerTag>(location, new TextMarkerTag(SourceTrail.FormatName));
            }
        }
        private void TrailChanged(object sender, TrailChange change)
        {
            if (!SourceTrail.IsCurrent(change.Generation)) return;
            string file = DocumentPath();
            if (change.File == null) Rebuild();
            else if (!string.Equals(file, change.File, StringComparison.OrdinalIgnoreCase)) return;
            else
            {
                lock (gate)
                {
                    if (disposed) return;
                    if (!string.Equals(path, file, StringComparison.OrdinalIgnoreCase) || generation != change.Generation) Rebuild();
                    else AddMarker(change.Line, change.Generation, file);
                }
            }
            Invalidate();
        }
        private void AddMarker(int number, long version, string owner)
        {
            if (!SourceTrail.IsCurrent(version) || generation != version || !string.Equals(path, owner, StringComparison.OrdinalIgnoreCase)) return;
            var snapshot = buffer.CurrentSnapshot;
            if (markers.ContainsKey(number) || number <= 0 || number > snapshot.LineCount) return;
            var line = snapshot.GetLineFromLineNumber(number - 1);
            var span = line.Length == 0 ? line.ExtentIncludingLineBreak : line.Extent;
            var marker = snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeExclusive);
            if (!disposed && SourceTrail.IsCurrent(version) && generation == version && string.Equals(path, owner, StringComparison.OrdinalIgnoreCase))
                markers[number] = marker;
        }
        private void Rebuild()
        {
            lock (gate)
            {
                if (disposed) return;
                markers.Clear(); string owner = DocumentPath(); path = owner;
                var lines = SourceTrail.Lines(owner, out var version); generation = version;
                foreach (int line in lines) AddMarker(line, version, owner);
            }
        }
        private string DocumentPath()
        {
            try { return document.FilePath; }
            catch (ObjectDisposedException) { return null; }
        }
        private void FileActionOccurred(object sender, TextDocumentFileActionEventArgs args)
        {
            if ((args.FileActionType & (FileActionTypes.DocumentRenamed | FileActionTypes.ContentLoadedFromDisk)) == 0) return;
            Rebuild(); Invalidate();
        }
        private void CurrentChanged(object sender, EventArgs args) { Invalidate(); }
        private void BufferChanged(object sender, TextContentChangedEventArgs args) { Invalidate(); }
        private void ViewClosed(object sender, EventArgs args) { Dispose(); }
        private void Invalidate()
        {
            if (disposed) return;
            var snapshot = buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; markers.Clear(); }
            SourceTrail.Changed -= TrailChanged;
            SourceHighlight.Changed -= CurrentChanged;
            view.Closed -= ViewClosed;
            buffer.Changed -= BufferChanged;
            document.FileActionOccurred -= FileActionOccurred;
        }
    }
}
