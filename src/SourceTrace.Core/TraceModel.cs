using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace SourceTrace.Core
{
    public enum DebugMode { Design, Running, Break, Unknown }
    public enum TraceStep { Into, Over, Out }
    public enum StopReason { Step, Breakpoint, Exception, UserBreak, ContextSwitch, Other }

    public sealed class DebugStopEventArgs : EventArgs
    {
        public DebugStopEventArgs(StopReason reason, string description)
        { Reason = reason; Description = description; }
        public StopReason Reason { get; }
        public string Description { get; }
    }

    public sealed class SourceSnapshot
    {
        public string Function { get; set; } = "";
        public string File { get; set; } = "";
        public string Source { get; set; } = "";
        public string Module { get; set; } = "";
        public int Line { get; set; }
        public int FrameIndex { get; set; }
        public string SourceState { get; set; } = "Unavailable";
        public int ProcessId { get; set; }
        public int ThreadId { get; set; }
        public bool HasSource => !string.IsNullOrWhiteSpace(File) && Line > 0;
        public SourceSnapshot Copy() => (SourceSnapshot)MemberwiseClone();
        public bool SameContext(SourceSnapshot other) => other != null &&
            ProcessId == other.ProcessId && ThreadId == other.ThreadId && FrameIndex == other.FrameIndex &&
            Line == other.Line && string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Function, other.Function, StringComparison.Ordinal) && string.Equals(Module, other.Module, StringComparison.Ordinal);
    }

    public sealed class TraceRecord : INotifyPropertyChanged
    {
        public long Sequence { get; set; }
        public DateTime TimestampUtc { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public string Operation { get; set; }
        public string Reason { get; set; }
        private SourceSnapshot location;
        public SourceSnapshot Location
        {
            get => location;
            set { location = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Location))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public TraceRecord Copy() => new TraceRecord { Sequence = Sequence, TimestampUtc = TimestampUtc,
            ElapsedMilliseconds = ElapsedMilliseconds, Operation = Operation, Reason = Reason, Location = Location.Copy() };
    }

    public sealed class TraceRecordEventArgs : EventArgs
    {
        public TraceRecordEventArgs(TraceRecord record) { Record = record; }
        public TraceRecord Record { get; }
    }

    public sealed class TraceExportSnapshot
    {
        public TraceExportSnapshot(IReadOnlyList<TraceRecord> records, int recordLimit, long droppedStops, bool interrupted)
        { Records = records; RecordLimit = recordLimit; DroppedStops = droppedStops; RecordingInterrupted = interrupted; }
        public IReadOnlyList<TraceRecord> Records { get; }
        public int RecordLimit { get; }
        public long DroppedStops { get; }
        public bool RecordingInterrupted { get; }
        public bool AtCapacity => Records.Count >= RecordLimit;
    }

    public sealed class TraceSettings
    {
        public int DelayMilliseconds { get; set; } = 30;
        public int MaxRecords { get; set; } = 100000;
        public bool PauseAtBreakpoints { get; set; } = true;
    }

    public interface ITraceDebugger
    {
        DebugMode Mode { get; }
        event EventHandler<DebugStopEventArgs> Stopped;
        event EventHandler Running;
        event EventHandler Ended;
        event EventHandler ContextChanged;
        SourceSnapshot Capture();
        void Step(TraceStep step);
        void Break();
    }

    // Callbacks run on the same UI thread as the debugger. Never run inline.
    public interface ITraceScheduler : IDisposable
    {
        void Schedule(Action callback, int delayMilliseconds);
        void Cancel();
    }
}
