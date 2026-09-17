using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;

namespace SourceTrace.Editor
{
    // Uses the editor's built-in result-location marker, including inactive editor views.
    internal sealed class NativeLineHighlight : IDisposable
    {
        private Lease current;
        private readonly Action clearManagedMarker;
        internal NativeLineHighlight(Action clearManagedMarker) { this.clearManagedMarker = clearManagedMarker; }
        internal bool HasMarker => current != null;

        internal void Show(IVsTextLines buffer, ITextDocument document, int line, int length, Func<bool> isCurrent)
        {
            if (!isCurrent()) return;
            var markers = new IVsTextLineMarker[1];
            ErrorHandler.ThrowOnFailure(buffer.CreateLineMarker((int)MARKERTYPE.MARKER_LIST_LOCATION, line, 0, line, length, null, markers));
            var marker = markers[0] ?? throw new InvalidOperationException("The editor did not create the source-line marker.");
            if (!isCurrent()) { TryInvalidate(marker); return; }
            Lease lease = null;
            lease = new Lease(marker, document, () => DocumentChanged(lease));
            var old = current; current = lease; old?.Dispose();
            if (!isCurrent()) { Remove(lease); return; }
            try
            {
                ErrorHandler.ThrowOnFailure(marker.GetVisualStyle(out uint style));
                if (!isCurrent()) { Remove(lease); return; }
                style = (style & ~(uint)MARKERVISUAL.MV_FORCE_INVISIBLE) |
                    (uint)(MARKERVISUAL.MV_COLOR_ALWAYS | MARKERVISUAL.MV_BORDER);
                ErrorHandler.ThrowOnFailure(marker.SetVisualStyle(style));
                if (!isCurrent()) Remove(lease);
            }
            catch { Remove(lease); throw; }
        }

        internal TextSpan? Span()
        {
            if (current == null) return null;
            var span = new TextSpan[1];
            return current.Marker.GetCurrentSpan(span) >= 0 ? span[0] : (TextSpan?)null;
        }
        internal void Clear()
        {
            // Detach before any event/COM callback so old cleanup cannot remove a newer lease.
            var old = current; current = null;
            try { clearManagedMarker(); }
            finally { old?.Dispose(); }
        }
        private void Remove(Lease lease)
        {
            if (ReferenceEquals(current, lease)) current = null;
            lease.Dispose();
        }
        private void DocumentChanged(Lease lease)
        {
            if (current != null && ReferenceEquals(current, lease)) Clear();
        }
        private static void TryInvalidate(IVsTextLineMarker marker)
        { try { marker.Invalidate(); } catch (Exception) { /* A closing document can already have invalidated its markers. */ } }
        public void Dispose() { Clear(); }

        private sealed class Lease : IDisposable
        {
            internal readonly IVsTextLineMarker Marker;
            internal readonly ITextDocument Document;
            private readonly Action renamed;
            private bool disposed;
            internal Lease(IVsTextLineMarker marker, ITextDocument document, Action renamed)
            {
                Marker = marker; Document = document; this.renamed = renamed;
                if (document != null) document.FileActionOccurred += FileAction;
            }
            private void FileAction(object sender, TextDocumentFileActionEventArgs args)
            { if ((args.FileActionType & FileActionTypes.DocumentRenamed) != 0) renamed(); }
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                if (Document != null) Document.FileActionOccurred -= FileAction;
                TryInvalidate(Marker);
            }
        }
    }
}
