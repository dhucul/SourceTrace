using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using SourceTrace.Core;
using SourceTrace.Debugging;
using SourceTrace.UI;
using SourceTrace.Editor;

namespace SourceTrace
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("SourceTrace", "Source statement tracing and execution history", "0.6.1")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(TraceToolWindow), Style = VsDockStyle.Linked, Orientation = ToolWindowOrientation.Top, Window = ToolWindowGuids80.Outputwindow, DockedHeight = 360, Height = 360)]
    [ProvideOptionPage(typeof(TraceOptions), "SourceTrace", "Tracing", 0, 0, true)]
    [Guid(PackageGuid)]
    public sealed partial class SourceTracePackage : AsyncPackage
    {
        public const string PackageGuid = "dc93d119-7e8b-4f22-8a19-8db4715101e1";
        public static readonly Guid CommandSet = new Guid("c5797592-4075-432d-881e-bd36aa9bdd94");
        private DteTraceDebugger debugger;
        private DTE dte;
        private TraceOptions options;
        private SourceTextReader sourceReader;
        private DispatcherTimer statePolling;
        private SourceFollower sourceFollower;
        private NativeLineHighlight nativeHighlight;
        private IVsEditorAdaptersFactoryService editorAdapters;
        private ITextDocumentFactoryService textDocuments;
        private DispatcherTraceScheduler windowRestore;
        private bool traceWindowRequested, traceWindowExplicitlyClosed, windowRestorePending;
        private DebugMode lastWindowMode;
        private int lastWindowRecordCount;
        internal SourceFollower SourceSelection => sourceFollower;
        internal TraceController Controller { get; private set; }
        internal OperationStatus ActivityStatus { get; } = new OperationStatus();
        internal CancellationToken ShutdownToken => DisposalToken;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            var dteService = await GetServiceAsync(typeof(DTE));
            var commands = (OleMenuCommandService)await GetServiceAsync(typeof(IMenuCommandService));
            var components = (IComponentModel)await GetServiceAsync(typeof(SComponentModel));
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            dte = dteService as DTE ?? throw new InvalidOperationException("The Visual Studio debugger service is unavailable.");
            if (commands == null) throw new InvalidOperationException("The Visual Studio command service is unavailable.");
            if (components == null) throw new InvalidOperationException("The Visual Studio editor service is unavailable.");
            editorAdapters = components.GetService<IVsEditorAdaptersFactoryService>();
            textDocuments = components.GetService<ITextDocumentFactoryService>();
            debugger = new DteTraceDebugger(dte);
            Controller = new TraceController(debugger, new DispatcherTraceScheduler(), new TraceSettings());
            options = (TraceOptions)GetDialogPage(typeof(TraceOptions));
            Controller.UpdateSettings(options.Snapshot());
            options.Applied += OptionsApplied;
            sourceReader = new SourceTextReader();
            nativeHighlight = new NativeLineHighlight(() => SourceHighlight.Set(null, 0));
            windowRestore = new DispatcherTraceScheduler();
            sourceFollower = new SourceFollower(new DispatcherTraceScheduler(), () => Controller.Records,
                NavigateToSource, nativeHighlight.Clear, ShowError);
            Controller.RecordAdded += RecordAdded;
            Controller.Changed += TraceStateChanged;
            statePolling = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            statePolling.Tick += PollDebugger;
            statePolling.Start();
            AddCommand(commands, 0x0100, () => ShowWindow());
            AddCommand(commands, 0x0101, () => Execute(() => Controller.Step(TraceStep.Into)));
            AddCommand(commands, 0x0102, () => Execute(() => Controller.Step(TraceStep.Over)));
            AddCommand(commands, 0x0103, () => Execute(() => Controller.Step(TraceStep.Out)));
            AddCommand(commands, 0x0104, () => Execute(() => Controller.ContinueTrace()));
            AddCommand(commands, 0x0105, PauseTrace);
        }

        private void AddCommand(OleMenuCommandService service, int id, Action action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = new OleMenuCommand((s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try { action(); }
                catch (Exception ex) { ShowError(ex); }
            }, new CommandID(CommandSet, id));
            command.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    command.Enabled = id == 0x0100 || (id == 0x0105 ? Controller.IsAutomatic || Controller.Mode == DebugMode.Running :
                        !Controller.IsStepOutstanding && !Controller.IsAutomatic && (Controller.Mode == DebugMode.Design || Controller.Mode == DebugMode.Break) &&
                        (id != 0x0102 && id != 0x0103 || Controller.Mode == DebugMode.Break));
                }
                catch (COMException) { command.Enabled = id == 0x0100; }
            };
            service.AddCommand(command);
        }

        internal void Execute(Action action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { ShowWindow(); action(); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void OptionsApplied(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Controller.UpdateSettings(options.Snapshot());
        }

        internal void PauseTrace()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Controller.Pause();
            try { ShowWindow(); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void PollDebugger(object sender, EventArgs args) { Controller.PollDebuggerState(); }
        private void RecordAdded(object sender, TraceRecordEventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SourceTrail.Add(args.Record.Location.File, args.Record.Location.Line);
            sourceFollower.Refresh();
            if (args.Record.Location.HasSource)
                JoinableTaskFactory.RunAsync(() => LoadSourceAsync(args.Record)).FileAndForget("SourceTrace/SourceText");
        }

        private void TraceStateChanged(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Controller.Records.Count == 0) SourceTrail.Clear();
            sourceFollower.Refresh();
            if (lastWindowMode != Controller.Mode || lastWindowRecordCount != Controller.Records.Count)
            {
                lastWindowMode = Controller.Mode;
                lastWindowRecordCount = Controller.Records.Count;
                QueueWindowRestore();
            }
        }

        internal void SetFollowSource(bool enabled) { sourceFollower.SetFollowing(enabled); }
        internal void SetSourceFilter(string filter) { sourceFollower.SetFilter(filter); }
        internal bool SelectSource(TraceRecord record) => sourceFollower.Select(record, false);
        private async Task LoadSourceAsync(TraceRecord record)
        {
            try
            {
                string text = await sourceReader.ReadAsync(record.Location.File, record.Location.Line).ConfigureAwait(false);
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                Controller.TrySetSource(record, text);
            }
            catch (OperationCanceledException) { }
        }

        private void ShowWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            traceWindowRequested = true;
            traceWindowExplicitlyClosed = false;
            var window = FindToolWindow(typeof(TraceToolWindow), 0, true);
            if (window?.Frame is IVsWindowFrame frame) { PinTraceWindow(frame); ErrorHandler.ThrowOnFailure(frame.Show()); }
        }

        private void QueueWindowRestore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if ((!traceWindowRequested && !traceWindowExplicitlyClosed) || windowRestorePending) return;
            windowRestorePending = true;
            windowRestore.Schedule(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                windowRestorePending = false;
                try { ApplyTraceWindowVisibility(); }
                catch (Exception ex) { LogError(ex); }
            }, 20);
        }
        private void ApplyTraceWindowVisibility()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Controller.IsDisposed) return;
            if (traceWindowExplicitlyClosed)
            {
                // The debugger can restore an older layout with this pane open. Respect the user's close.
                var closedWindow = FindToolWindow(typeof(TraceToolWindow), 0, false);
                if (closedWindow?.Frame is IVsWindowFrame closedFrame && closedFrame.IsVisible() == VSConstants.S_OK)
                    ErrorHandler.ThrowOnFailure(closedFrame.Hide());
                return;
            }
            if (!traceWindowRequested) return;
            var window = FindToolWindow(typeof(TraceToolWindow), 0, true);
            if (window?.Frame is IVsWindowFrame frame)
            { PinTraceWindow(frame); ErrorHandler.ThrowOnFailure(frame.ShowNoActivate()); }
        }
        private static void PinTraceWindow(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_ExtWindowObject, out var value) >= 0 && value is EnvDTE.Window window)
            {
                if (window.AutoHides) window.AutoHides = false;
            }
        }
        internal void TraceWindowClosed()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            traceWindowRequested = false; traceWindowExplicitlyClosed = true;
            windowRestore?.Cancel(); windowRestorePending = false;
        }

        internal bool OpenSource(TraceRecord record)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return sourceFollower.Select(record, true);
        }

        private void NavigateToSource(SourceNavigationRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!request.IsCurrent) return;
            var location = request.Location;
            if (!location.HasSource) { request.TryCommit(nativeHighlight.Clear); return; }
            var previousFocus = Keyboard.FocusedElement;
            bool restoreFocus = !request.ActivateEditor && IsTraceControlFocus(previousFocus as DependencyObject);
            try
            {
                VsShellUtilities.OpenDocument(this, location.File, VSConstants.LOGVIEWID_Code, out _, out _, out var frame, out var view);
                if (!request.IsCurrent) return;
                if (view == null) throw new InvalidOperationException("The source document does not have a text editor view.");
                ErrorHandler.ThrowOnFailure(view.GetBuffer(out var buffer));
                if (!request.IsCurrent) return;
                ErrorHandler.ThrowOnFailure(buffer.GetLineCount(out int lineCount));
                if (!request.IsCurrent) return;
                if (location.Line > lineCount) throw new InvalidOperationException("The recorded source line is no longer in this document.");
                int line = location.Line - 1;
                ErrorHandler.ThrowOnFailure(buffer.GetLengthOfLine(line, out int length));
                if (!request.IsCurrent) return;
                ErrorHandler.ThrowOnFailure(view.SetCaretPos(line, 0));
                if (!request.IsCurrent) return;
                ErrorHandler.ThrowOnFailure(view.SetSelection(line, 0, line, 0));
                if (!request.IsCurrent) return;
                // Showing an entire long line can scroll its executable statement offscreen to the right.
                ErrorHandler.ThrowOnFailure(view.EnsureSpanVisible(new TextSpan { iStartLine = line, iEndLine = line, iStartIndex = 0, iEndIndex = Math.Min(length, 1) }));
                if (!request.IsCurrent) return;
                if (request.ActivateEditor) ErrorHandler.ThrowOnFailure(frame.Show());
                else ErrorHandler.ThrowOnFailure(frame.ShowNoActivate());
                if (!request.IsCurrent) return;
                var dataBuffer = editorAdapters.GetDataBuffer((IVsTextBuffer)buffer);
                ITextDocument document = null;
                if (dataBuffer != null) textDocuments.TryGetTextDocument(dataBuffer, out document);
                if (!request.IsCurrent) return;
                nativeHighlight.Show(buffer, document, line, length, () => request.IsCurrent);
                request.TryCommit(() => SourceHighlight.Set(location.File, location.Line));
            }
            finally
            {
                try { ApplyTraceWindowVisibility(); }
                catch (Exception ex) { LogError(ex); }
                if (request.IsCurrent && restoreFocus && previousFocus is FrameworkElement element && element.IsVisible)
                {
                    try { Keyboard.Focus(previousFocus); }
                    catch (Exception ex) { LogError(ex); }
                }
            }
        }

        private static bool IsTraceControlFocus(DependencyObject element)
        {
            while (element != null)
            {
                if (element is TraceControl) return true;
                element = element is Visual || element is Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        internal void ShowOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { ShowOptionPage(typeof(TraceOptions)); }
            catch (Exception ex) { ShowError(ex); }
        }
        internal void ShowError(Exception ex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ActivityStatus.Begin(ex.Message);
            LogError(ex);
            try { if (dte != null) dte.StatusBar.Text = "SourceTrace: " + ex.Message; }
            catch (Exception) { /* The IDE's status bar may already be disposed. */ }
        }

        internal static void LogError(Exception ex)
        {
            try { ActivityLog.LogError("SourceTrace", ex.ToString()); }
            catch (Exception) { /* Error reporting must not throw during shutdown. */ }
        }

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                if (statePolling != null) { statePolling.Stop(); statePolling.Tick -= PollDebugger; }
                if (options != null) options.Applied -= OptionsApplied;
                if (Controller != null) { Controller.RecordAdded -= RecordAdded; Controller.Changed -= TraceStateChanged; }
                sourceFollower?.Dispose();
                windowRestore?.Dispose();
                nativeHighlight?.Dispose();
                SourceTrail.Clear();
                Controller?.Dispose();
                sourceReader?.Dispose();
                debugger?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
