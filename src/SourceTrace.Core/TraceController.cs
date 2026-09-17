using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SourceTrace.Core
{
    // All entry points, notifications and scheduler callbacks run on the debugger UI thread.
    public sealed class TraceController : IDisposable
    {
        public const int CompletionRecoveryMilliseconds = 15000;
        private readonly ITraceDebugger debugger;
        private readonly ITraceScheduler scheduler;
        private readonly List<TraceRecord> records = new List<TraceRecord>();
        private readonly Stopwatch elapsed = new Stopwatch();
        private readonly Func<long> clock;
        private bool disposed, currentStopRecorded, stepOutstanding;
        private int generation;
        private long stepStarted;
        private string operation = "Debugger";
        private SourceSnapshot lastContext;

        public TraceController(ITraceDebugger debugger, ITraceScheduler scheduler, TraceSettings settings, Func<long> monotonicMilliseconds = null)
        {
            this.debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            clock = monotonicMilliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)));
            Records = records.AsReadOnly();
            debugger.Stopped += OnStopped;
            debugger.Running += OnRunning;
            debugger.Ended += OnEnded;
            debugger.ContextChanged += OnContextChanged;
            TryReadMode();
        }

        public TraceSettings Settings { get; }
        public IReadOnlyList<TraceRecord> Records { get; }
        public DebugMode Mode { get; private set; } = DebugMode.Unknown;
        public bool IsAutomatic { get; private set; }
        public bool IsStepOutstanding => stepOutstanding;
        public bool IsDisposed => disposed;
        public bool AtCapacity => records.Count >= Math.Max(1, Settings.MaxRecords);
        public long DroppedStops { get; private set; }
        public bool RecordingInterrupted { get; private set; }
        public long SourceRevision { get; private set; }
        public string Status { get; private set; } = "Ready. Use Trace Into to begin, or pause at a breakpoint.";
        public string RecordingNotice => DroppedStops > 0 ? "INCOMPLETE TRACE: " + DroppedStops + " stops omitted at the record limit. Export and clear to collect more." :
            AtCapacity ? "Record limit reached. Recording is suspended until space is available. Export and clear to continue." :
            RecordingInterrupted ? "Trace may be incomplete: debugger state recovery was required." : "";
        public event EventHandler Changed;
        public event EventHandler<TraceRecordEventArgs> RecordAdded;

        public void Step(TraceStep step) => Guard(() =>
        {
            CancelAutomatic();
            int ticket = generation;
            if (!CanStep(step) || !Owns(ticket)) return;
            if (Mode == DebugMode.Break)
            {
                var current = ReadCurrent();
                if (!Owns(ticket)) return;
                EnsureCurrentRecorded(current);
            }
            if (!Owns(ticket)) return;
            if (AtCapacity) { SetStatus(RecordingNotice); return; }
            IssueStep(step, ticket);
        });

        public void ContinueTrace() => Guard(() =>
        {
            if (IsAutomatic) return;
            CancelAutomatic();
            int ticket = generation;
            if (!CanStep(TraceStep.Into) || !Owns(ticket)) return;
            if (Mode == DebugMode.Break)
            {
                // Always inspect the live frame; history deduplication must not determine eligibility.
                var current = ReadCurrent();
                if (!Owns(ticket)) return;
                EnsureCurrentRecorded(current);
                if (!Owns(ticket)) return;
                if (!current.HasSource) { SetStatus("Source is unavailable. Load symbols/source or use Trace Out, then retry."); return; }
            }
            if (AtCapacity) { SetStatus(RecordingNotice); return; }
            IsAutomatic = true;
            QueueStep();
        });

        public void Pause() => Guard(() =>
        {
            // Cancellation takes effect before any COM operation or optional UI work.
            CancelAutomatic();
            int ticket = generation;
            if (!TryReadMode() || !Owns(ticket)) return;
            if (Mode == DebugMode.Running)
            {
                SetStatus("Automatic tracing stopped. Waiting for the debugger to pause...");
                if (!Owns(ticket)) return;
                debugger.Break();
            }
            else SetStatus(stepOutstanding ? "Automatic tracing stopped. Waiting for the outstanding debugger command." : "Trace paused.");
        });

        public void Clear() => Guard(() =>
        {
            if (!TryReadMode()) return;
            if (stepOutstanding || Mode == DebugMode.Running)
            { SetStatus("Pause and wait for the current debugger command before clearing the trace."); return; }
            CancelAutomatic();
            records.Clear();
            elapsed.Reset();
            currentStopRecorded = false;
            lastContext = null;
            DroppedStops = 0;
            RecordingInterrupted = false;
            SourceRevision++;
            SetStatus("Trace cleared.");
        });

        public void UpdateSettings(TraceSettings updated) => Guard(() =>
        {
            if (updated == null) throw new ArgumentNullException(nameof(updated));
            Settings.DelayMilliseconds = Math.Max(1, Math.Min(5000, updated.DelayMilliseconds));
            Settings.MaxRecords = Math.Max(1, Math.Min(1000000, updated.MaxRecords));
            Settings.PauseAtBreakpoints = updated.PauseAtBreakpoints;
            if (AtCapacity && IsAutomatic)
            {
                Pause();
                SetStatus(RecordingNotice);
            }
            else if (IsAutomatic && !stepOutstanding) QueueStep();
            else Notify();
        });

        // Called periodically even when the tool window is hidden. Mode is cached for UI consumers.
        public void PollDebuggerState() => Guard(() =>
        {
            if (!TryReadMode()) return;
            if (stepOutstanding && Mode == DebugMode.Running) stepStarted = clock();
            if (stepOutstanding && Mode != DebugMode.Running && clock() - stepStarted >= CompletionRecoveryMilliseconds)
            {
                CancelAutomatic();
                stepOutstanding = false;
                currentStopRecorded = false;
                RecordingInterrupted = true;
                if (Mode == DebugMode.Break) RecordStop(ReadCurrent(), "Recovery", "Completion event unavailable");
                SetStatus("Debugger command did not report completion. Automatic tracing stopped; retry when Visual Studio is ready.");
            }
        });

        public void ReportError(string message)
        {
            if (disposed) return;
            Fail(message);
        }

        public TraceExportSnapshot CreateExportSnapshot()
        {
            var copy = new TraceRecord[records.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = records[i].Copy();
            return new TraceExportSnapshot(copy, Math.Max(1, Settings.MaxRecords), DroppedStops, RecordingInterrupted);
        }

        public bool TrySetSource(TraceRecord record, string text)
        {
            // An old worker result must never attach to a new row after Clear or disposal.
            if (disposed || record == null || record.Sequence < 1 || record.Sequence > records.Count ||
                !ReferenceEquals(records[(int)record.Sequence - 1], record)) return false;
            var location = record.Location.Copy();
            location.Source = text ?? "";
            location.SourceState = text == null ? "Unavailable" : "Available";
            record.Location = location;
            SourceRevision++;
            Notify();
            return true;
        }

        private bool CanStep(TraceStep step)
        {
            if (!TryReadMode()) return false;
            if (stepOutstanding || Mode == DebugMode.Running)
            { SetStatus("Wait for the current debugger command, or use Pause."); return false; }
            if (Mode == DebugMode.Design && step != TraceStep.Into)
            { SetStatus("Start debugging with Trace Into before using Trace Over or Trace Out."); return false; }
            return Mode == DebugMode.Design || Mode == DebugMode.Break;
        }

        private bool Owns(int ticket) => !disposed && generation == ticket;

        private void IssueStep(TraceStep step, int ticket)
        {
            if (!Owns(ticket) || stepOutstanding) return;
            // Notifications and debugger reads can reenter the controller on the UI thread.
            // Publish status before claiming a command so cancellation cannot leave a phantom step.
            SetStatus(IsAutomatic ? "Tracing source statements..." : "Waiting for Trace " + step + "...");
            if (!Owns(ticket) || stepOutstanding || AtCapacity ||
                (Mode != DebugMode.Design && Mode != DebugMode.Break)) return;
            operation = (IsAutomatic ? "Continue / " : "Trace ") + step;
            stepOutstanding = true;
            stepStarted = clock();
            currentStopRecorded = false;
            try { debugger.Step(step); }
            catch (Exception ex)
            {
                // Only a failure from this command can release its pending state. A stale
                // exception must not settle a newer command or undo a reentrant cancellation.
                if (!Owns(ticket)) return;
                stepOutstanding = false;
                Fail("Debugger operation failed: " + ex.Message);
            }
        }

        private void QueueStep()
        {
            var ticket = ++generation;
            bool mayLaunch = Mode == DebugMode.Design;
            SetStatus("Tracing source statements...");
            if (!Owns(ticket) || !IsAutomatic) return;
            scheduler.Schedule(() => Guard(() =>
            {
                if (!Owns(ticket) || !IsAutomatic) return;
                if (!TryReadMode() || !Owns(ticket) || !IsAutomatic) return;
                if (Mode == DebugMode.Running || stepOutstanding)
                { CancelAutomatic(); SetStatus("Trace paused because the debugger resumed outside SourceTrace."); return; }
                if (Mode == DebugMode.Design && !mayLaunch)
                { CancelAutomatic(); SetStatus("Debug session ended before the next trace step."); return; }
                if (AtCapacity) { CancelAutomatic(); SetStatus(RecordingNotice); return; }
                if (Mode == DebugMode.Break)
                {
                    var current = ReadCurrent();
                    if (!Owns(ticket) || !IsAutomatic) return;
                    if (lastContext != null && !current.SameContext(lastContext))
                    { InvalidateContext(current); return; }
                    if (!current.HasSource) { CancelAutomatic(); SetStatus("Trace paused: source is unavailable."); return; }
                }
                IssueStep(TraceStep.Into, ticket);
            }), Math.Max(1, Settings.DelayMilliseconds));
        }

        private void OnStopped(object sender, DebugStopEventArgs args) => Guard(() =>
        {
            Mode = DebugMode.Break;
            if (args.Reason == StopReason.ContextSwitch) { stepOutstanding = false; InvalidateContext(ReadCurrent()); return; }
            bool wasOurStep = stepOutstanding;
            stepOutstanding = false;
            currentStopRecorded = false;
            var current = ReadCurrent();
            RecordStop(current, wasOurStep ? operation : "Debugger", args.Description);
            if (AtCapacity) { CancelAutomatic(); SetStatus(RecordingNotice); return; }
            if (!IsAutomatic) { SetStatus("Paused: " + args.Description + "."); return; }
            if (args.Reason == StopReason.Exception || args.Reason == StopReason.UserBreak ||
                (args.Reason == StopReason.Breakpoint && Settings.PauseAtBreakpoints))
            { CancelAutomatic(); SetStatus("Trace paused: " + args.Description + ". Choose Continue Trace to resume."); return; }
            if (!wasOurStep || (args.Reason != StopReason.Step && args.Reason != StopReason.Breakpoint))
            { CancelAutomatic(); SetStatus("Trace paused at an unexpected debugger stop: " + args.Description + "."); return; }
            if (!current.HasSource)
            { CancelAutomatic(); SetStatus("Trace paused: source is unavailable. Load symbols/source, or use Trace Out."); return; }
            QueueStep();
        });

        private void OnContextChanged(object sender, EventArgs args) => Guard(() =>
        {
            if (!TryReadMode()) return;
            // Stepping itself emits context notifications. Only compare stable, completed stops.
            if (Mode != DebugMode.Break || stepOutstanding) return;
            var current = ReadCurrent();
            if (lastContext == null || !current.SameContext(lastContext)) InvalidateContext(current);
        });

        private void InvalidateContext(SourceSnapshot current)
        {
            CancelAutomatic();
            currentStopRecorded = false;
            lastContext = current;
            SetStatus("Trace paused after a debugger context change.");
        }

        private SourceSnapshot ReadCurrent() => (debugger.Capture() ?? new SourceSnapshot()).Copy();
        private void EnsureCurrentRecorded(SourceSnapshot current)
        {
            if (!currentStopRecorded || !current.SameContext(lastContext)) RecordStop(current, "Start", "Current statement", false);
        }
        private void RecordStop(SourceSnapshot current, string action, string reason, bool actualStop = true)
        {
            lastContext = current.Copy();
            currentStopRecorded = true;
            if (AtCapacity) { if (actualStop) DroppedStops++; Notify(); return; }
            if (!elapsed.IsRunning) elapsed.Start();
            var record = new TraceRecord { Sequence = records.Count + 1, TimestampUtc = DateTime.UtcNow,
                ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, Operation = action, Reason = reason, Location = current.Copy() };
            records.Add(record);
            RecordAdded?.Invoke(this, new TraceRecordEventArgs(record));
            Notify();
        }

        private void OnRunning(object sender, EventArgs args) => Guard(() =>
        {
            Mode = DebugMode.Running;
            currentStopRecorded = false;
            if (IsAutomatic && !stepOutstanding)
            { CancelAutomatic(); SetStatus("Trace paused because the debugger resumed outside SourceTrace."); }
            else Notify();
        });

        private void OnEnded(object sender, EventArgs args) => Guard(() =>
        {
            CancelAutomatic();
            Mode = DebugMode.Design;
            stepOutstanding = false;
            currentStopRecorded = false;
            lastContext = null;
            elapsed.Stop();
            SetStatus("Debug session ended. " + records.Count + " source stops retained for review and export.");
        });

        private bool TryReadMode()
        {
            int ticket = generation;
            try
            {
                var current = debugger.Mode;
                if (!Owns(ticket)) return false;
                if (current == DebugMode.Unknown) { Mode = current; Fail("Debugger state is unavailable."); return false; }
                if (Mode != current) { Mode = current; Notify(); }
                return Owns(ticket);
            }
            catch (Exception ex)
            {
                if (Owns(ticket)) { Mode = DebugMode.Unknown; Fail("Debugger state unavailable: " + ex.Message); }
                return false;
            }
        }
        private void Guard(Action action)
        {
            if (disposed) return;
            try { action(); }
            catch (Exception ex) { Fail("Debugger operation failed: " + ex.Message); }
        }
        private void Fail(string message)
        {
            if (disposed) return;
            CancelAutomatic();
            // A failed state read or callback does not mean an asynchronous command finished.
            // Keep its guard and recovery timer until completion, session end, or recovery.
            currentStopRecorded = false;
            RecordingInterrupted = true;
            SetStatus(message);
        }
        private void CancelAutomatic() { IsAutomatic = false; ++generation; scheduler.Cancel(); }
        private void SetStatus(string message) { Status = message; Notify(); }
        private void Notify() { Changed?.Invoke(this, EventArgs.Empty); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CancelAutomatic();
            debugger.Stopped -= OnStopped;
            debugger.Running -= OnRunning;
            debugger.Ended -= OnEnded;
            debugger.ContextChanged -= OnContextChanged;
            scheduler.Dispose();
        }
    }
}
