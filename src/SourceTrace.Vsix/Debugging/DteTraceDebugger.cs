using System;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE90a;
using Microsoft.VisualStudio.Shell;
using SourceTrace.Core;

namespace SourceTrace.Debugging
{
    internal sealed class DteTraceDebugger : ITraceDebugger, IDisposable
    {
        private readonly DTE dte;
        // COM event sources must stay alive for the lifetime of the subscription.
        private readonly Events events;
        private readonly DebuggerEvents debuggerEvents;

        public DteTraceDebugger(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.dte = dte;
            events = dte.Events;
            debuggerEvents = events.DebuggerEvents;
            debuggerEvents.OnEnterBreakMode += OnBreak;
            debuggerEvents.OnEnterRunMode += OnRun;
            debuggerEvents.OnEnterDesignMode += OnEnd;
            debuggerEvents.OnContextChanged += OnContext;
        }

        public DebugMode Mode
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var mode = dte.Debugger.CurrentMode;
                return mode == dbgDebugMode.dbgBreakMode ? DebugMode.Break : mode == dbgDebugMode.dbgRunMode ? DebugMode.Running :
                    mode == dbgDebugMode.dbgDesignMode ? DebugMode.Design : DebugMode.Unknown;
            }
        }

        public event EventHandler<DebugStopEventArgs> Stopped;
        public event EventHandler Running;
        public event EventHandler Ended;
        public event EventHandler ContextChanged;

        public void Step(TraceStep step)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Never wait synchronously for a debugger stop on the UI thread.
            if (step == TraceStep.Over) dte.Debugger.StepOver(false);
            else if (step == TraceStep.Out) dte.Debugger.StepOut(false);
            else dte.Debugger.StepInto(false);
        }

        public void Break()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            dte.Debugger.Break(false);
        }

        public SourceSnapshot Capture()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var frame = dte.Debugger.CurrentStackFrame;
            if (frame == null) return new SourceSnapshot();
            var result = new SourceSnapshot
            {
                Function = Read(() => { ThreadHelper.ThrowIfNotOnUIThread(); return frame.FunctionName; }, ""),
                Module = Read(() => { ThreadHelper.ThrowIfNotOnUIThread(); return frame.Module; }, ""),
                ThreadId = Read(() => { ThreadHelper.ThrowIfNotOnUIThread(); return dte.Debugger.CurrentThread.ID; }, 0),
                ProcessId = Read(() => { ThreadHelper.ThrowIfNotOnUIThread(); return dte.Debugger.CurrentProcess.ProcessID; }, 0)
            };
            var frame2 = frame as StackFrame2;
            if (frame2 != null)
            {
                // Read the actual debug frame, never the active editor or caret.
                result.File = Read(() => { ThreadHelper.ThrowIfNotOnUIThread(); return frame2.FileName; }, "");
                result.Line = (int)Read(() => { ThreadHelper.ThrowIfNotOnUIThread(); return frame2.LineNumber; }, 0u);
                result.FrameIndex = (int)Read(() => { ThreadHelper.ThrowIfNotOnUIThread(); return frame2.Depth; }, 0u);
                result.SourceState = result.HasSource ? "Pending" : "Unavailable";
            }
            return result;
        }

        private static T Read<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch (COMException) { return fallback; }
            catch (NotImplementedException) { return fallback; }
        }

        private void OnBreak(dbgEventReason reason, ref dbgExecutionAction executionAction)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var mapped = StopReason.Other;
            if (reason == dbgEventReason.dbgEventReasonStep) mapped = StopReason.Step;
            else if (reason == dbgEventReason.dbgEventReasonBreakpoint) mapped = StopReason.Breakpoint;
            else if (reason == dbgEventReason.dbgEventReasonExceptionThrown || reason == dbgEventReason.dbgEventReasonExceptionNotHandled) mapped = StopReason.Exception;
            else if (reason == dbgEventReason.dbgEventReasonUserBreak) mapped = StopReason.UserBreak;
            else if (reason == dbgEventReason.dbgEventReasonContextSwitch) mapped = StopReason.ContextSwitch;
            Stopped?.Invoke(this, new DebugStopEventArgs(mapped, reason.ToString().Replace("dbgEventReason", "")));
        }

        private void OnRun(dbgEventReason reason) { ThreadHelper.ThrowIfNotOnUIThread(); Running?.Invoke(this, EventArgs.Empty); }
        private void OnEnd(dbgEventReason reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Ended?.Invoke(this, EventArgs.Empty);
        }

        private void OnContext(Process process, Program program, EnvDTE.Thread thread, StackFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ContextChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            debuggerEvents.OnEnterBreakMode -= OnBreak;
            debuggerEvents.OnEnterRunMode -= OnRun;
            debuggerEvents.OnEnterDesignMode -= OnEnd;
            debuggerEvents.OnContextChanged -= OnContext;
        }
    }
}
