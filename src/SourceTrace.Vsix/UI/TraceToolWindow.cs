using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace SourceTrace.UI
{
    [Guid("53f3a0c0-8874-4c1f-9ac1-f23831951c96")]
    public sealed class TraceToolWindow : ToolWindowPane, IVsWindowFrameNotify, IVsWindowFrameNotify2, IVsWindowFrameNotify3
    {
        private IVsWindowFrame2 notificationFrame;
        private uint notificationCookie;
        public TraceToolWindow() : base(null)
        {
            Caption = "SourceTrace";
            // The pane requires content before Visual Studio creates its window frame.
            Content = new TraceControl();
        }
        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            ThreadHelper.ThrowIfNotOnUIThread();
            ((TraceControl)Content).Initialize((SourceTracePackage)Package);
            notificationFrame = Frame as IVsWindowFrame2;
            if (notificationFrame != null) ErrorHandler.ThrowOnFailure(notificationFrame.Advise(this, out notificationCookie));
        }
        public int OnShow(int show)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Persistent tool windows can close without destroying the pane or raising OnClose.
            if (show == (int)__FRAMESHOW.FRAMESHOW_WinClosed)
                ((SourceTracePackage)Package).TraceWindowClosed();
            return VSConstants.S_OK;
        }
        public int OnMove() => VSConstants.S_OK;
        public int OnSize() => VSConstants.S_OK;
        public int OnDockableChange(int dockable) => VSConstants.S_OK;
        public int OnMove(int x, int y, int width, int height) => VSConstants.S_OK;
        public int OnSize(int x, int y, int width, int height) => VSConstants.S_OK;
        public int OnDockableChange(int dockable, int x, int y, int width, int height) => VSConstants.S_OK;
        public int OnClose(ref uint options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ((SourceTracePackage)Package).TraceWindowClosed();
            return VSConstants.S_OK;
        }
        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing && notificationFrame != null)
            { notificationFrame.Unadvise(notificationCookie); notificationFrame = null; }
            base.Dispose(disposing);
        }
    }
}
