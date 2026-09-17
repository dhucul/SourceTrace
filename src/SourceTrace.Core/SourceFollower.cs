using System;
using System.Collections.Generic;

namespace SourceTrace.Core
{
    public sealed class SourceNavigationRequest
    {
        private readonly Func<bool> isCurrent;
        internal SourceNavigationRequest(SourceSnapshot location, bool activateEditor, Func<bool> isCurrent)
        { Location = location; ActivateEditor = activateEditor; this.isCurrent = isCurrent; }
        public SourceSnapshot Location { get; }
        public bool ActivateEditor { get; }
        // UI-thread only. Recheck after calls into Visual Studio that may pump nested messages.
        public bool IsCurrent => isCurrent();
        public bool TryCommit(Action action)
        {
            if (!IsCurrent) return false;
            action();
            return IsCurrent;
        }
    }

    // Owns the selected record for both the grid and editor. All calls use the UI thread.
    public sealed class SourceFollower : IDisposable
    {
        public const int VisibleRecordLimit = 5000;
        private readonly ITraceScheduler scheduler;
        private readonly Func<IReadOnlyList<TraceRecord>> getRecords;
        private readonly Action<SourceNavigationRequest> navigate;
        private readonly Action clearHighlight;
        private readonly Action<Exception> reportError;
        private SourceSnapshot selectedLocation;
        private long generation;
        private bool disposed;

        public SourceFollower(ITraceScheduler scheduler, Func<IReadOnlyList<TraceRecord>> getRecords,
            Action<SourceNavigationRequest> navigate, Action clearHighlight, Action<Exception> reportError)
        {
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            this.getRecords = getRecords ?? throw new ArgumentNullException(nameof(getRecords));
            this.navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
            this.clearHighlight = clearHighlight ?? throw new ArgumentNullException(nameof(clearHighlight));
            this.reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        }

        public bool Following { get; private set; } = true;
        public string Filter { get; private set; } = "";
        public TraceRecord SelectedRecord { get; private set; }
        public event EventHandler Changed;

        public static bool Matches(TraceRecord record, string filter)
        {
            if (record?.Location == null) return false;
            string query = (filter ?? "").Trim();
            return query.Length == 0 || Contains(record.Location.Function, query) || Contains(record.Location.File, query) ||
                Contains(record.Location.Source, query) || Contains(record.Reason, query);
        }
        private static bool Contains(string value, string query) => value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        public bool IsSelectable(TraceRecord record)
        {
            if (disposed || record == null) return false;
            var records = getRecords();
            return record.Sequence > Math.Max(0, records.Count - VisibleRecordLimit) && record.Sequence <= records.Count &&
                ReferenceEquals(records[(int)record.Sequence - 1], record) && Matches(record, Filter);
        }

        public void Refresh()
        {
            if (disposed) return;
            var next = Following ? LatestMatching() : IsSelectable(SelectedRecord) ? SelectedRecord : null;
            SetTarget(next, false, false);
        }
        public void SetFilter(string filter)
        {
            if (disposed) return;
            string next = (filter ?? "").Trim();
            if (Filter == next) return;
            Filter = next;
            SetTarget(Following ? LatestMatching() : IsSelectable(SelectedRecord) ? SelectedRecord : null, false, true);
        }
        public void SetFollowing(bool enabled)
        {
            if (disposed) return;
            Following = enabled;
            if (enabled) SetTarget(LatestMatching(), false, true);
            // Freezing the selection must still let its queued navigation finish.
            else Notify();
        }
        public bool Select(TraceRecord record, bool activateEditor)
        {
            // Reject stale grid rows before changing follow mode or canceling a newer request.
            if (!IsSelectable(record)) return false;
            Following = false;
            SetTarget(record, activateEditor, true);
            return true;
        }

        private TraceRecord LatestMatching()
        {
            var records = getRecords();
            for (int i = records.Count - 1, first = Math.Max(0, records.Count - VisibleRecordLimit); i >= first; i--)
                if (Matches(records[i], Filter)) return records[i];
            return null;
        }
        private void SetTarget(TraceRecord record, bool activateEditor, bool force)
        {
            if (!force && ReferenceEquals(record, SelectedRecord) &&
                (record == null || record.Location.SameContext(selectedLocation))) return;
            Cancel();
            SelectedRecord = record;
            selectedLocation = record?.Location.Copy();
            long ticket = generation;
            ClearMarker();
            if (!Owns(ticket, record)) return;
            Notify();
            if (!Owns(ticket, record) || record == null) return;
            var request = new SourceNavigationRequest(selectedLocation.Copy(), activateEditor, () => Owns(ticket, record));
            scheduler.Schedule(() =>
            {
                if (!request.IsCurrent) return;
                try { navigate(request); }
                catch (Exception ex)
                {
                    if (!request.IsCurrent) return;
                    ClearMarker();
                    if (request.IsCurrent) Report(ex);
                }
            }, 1);
        }
        private bool Owns(long ticket, TraceRecord record) => !disposed && generation == ticket &&
            ReferenceEquals(SelectedRecord, record) && (record == null ||
                (IsSelectable(record) && record.Location.SameContext(selectedLocation)));
        private void Cancel() { ++generation; scheduler.Cancel(); }
        private void Notify()
        {
            if (Changed == null) return;
            foreach (EventHandler handler in Changed.GetInvocationList())
            { try { handler(this, EventArgs.Empty); } catch (Exception ex) { Report(ex); } }
        }
        private void ClearMarker() { try { clearHighlight(); } catch (Exception ex) { Report(ex); } }
        private void Report(Exception ex) { try { reportError(ex); } catch (Exception) { /* Reporting cannot escape into the dispatcher. */ } }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Cancel();
            SelectedRecord = null;
            ClearMarker();
            scheduler.Dispose();
        }
    }
}
