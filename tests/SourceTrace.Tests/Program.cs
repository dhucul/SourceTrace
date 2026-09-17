using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using SourceTrace.Core;

internal static class Program
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Test("Manual Into, Over, and Out call the requested debugger operation", () =>
        {
            using var h = new Harness();
            foreach (var step in new[] { TraceStep.Into, TraceStep.Over, TraceStep.Out })
            { h.Controller.Step(step); Equal(step, h.Debugger.Steps.Last()); h.Debugger.Stop(); }
            Equal(4, h.Controller.Records.Count); // Initial location plus three executed steps.
        });
        Test("Trace Into can launch a debug session", () =>
        {
            using var h = new Harness(DebugMode.Design);
            h.Controller.Step(TraceStep.Into);
            Equal(1, h.Debugger.Steps.Count); Equal(0, h.Controller.Records.Count);
            h.Debugger.Stop(); Equal(1, h.Controller.Records.Count);
        });
        Test("Over and Out cannot launch a session", () =>
        {
            using var h = new Harness(DebugMode.Design);
            h.Controller.Step(TraceStep.Over); h.Controller.Step(TraceStep.Out); Equal(0, h.Debugger.Steps.Count);
        });
        Test("Commands cannot overlap an in-flight step", () =>
        {
            using var h = new Harness();
            h.Controller.Step(TraceStep.Into); h.Controller.Step(TraceStep.Over); h.Controller.ContinueTrace();
            Equal(1, h.Debugger.Steps.Count);
        });
        Test("Continue launches from design and queues each source step", () =>
        {
            using var h = new Harness(DebugMode.Design);
            h.Controller.ContinueTrace(); Equal(0, h.Debugger.Steps.Count);
            h.Scheduler.Fire(); Equal(1, h.Debugger.Steps.Count);
            h.Debugger.Stop(); Equal(1, h.Debugger.Steps.Count); // No debugger calls inside a stop event.
            h.Scheduler.Fire(); Equal(2, h.Debugger.Steps.Count);
        });
        Test("Repeated Continue does not start multiple step loops", () =>
        {
            using var h = new Harness();
            h.Controller.ContinueTrace(); var pending = h.Scheduler.Pending;
            h.Controller.ContinueTrace(); Equal(pending, h.Scheduler.Pending); Equal(1, h.Controller.Records.Count);
        });
        Test("Pause cancels even a stale queued callback", () =>
        {
            using var h = new Harness();
            h.Controller.ContinueTrace(); var stale = h.Scheduler.Pending; h.Controller.Pause(); stale();
            Equal(0, h.Debugger.Steps.Count); Equal(false, h.Controller.IsAutomatic);
        });
        Test("Pause during a step breaks the program and does not resume", () =>
        {
            using var h = new Harness(); h.StartAutomatic(); h.Controller.Pause();
            Equal(1, h.Debugger.BreakCalls); h.Debugger.Stop(StopReason.UserBreak);
            Equal(false, h.Controller.IsAutomatic); Equal(null, h.Scheduler.Pending);
        });
        Test("Breakpoints pause by default", () =>
        {
            using var h = new Harness(); h.StartAutomatic(); h.Debugger.Stop(StopReason.Breakpoint);
            Equal(false, h.Controller.IsAutomatic); Equal(null, h.Scheduler.Pending);
        });
        Test("Breakpoint continuation is configurable", () =>
        {
            using var h = new Harness(); h.Controller.Settings.PauseAtBreakpoints = false;
            h.StartAutomatic(); h.Debugger.Stop(StopReason.Breakpoint);
            Equal(true, h.Controller.IsAutomatic); Check(h.Scheduler.Pending != null);
        });
        foreach (var reason in new[] { StopReason.Exception, StopReason.UserBreak, StopReason.Other })
        {
            Test(reason + " always pauses continuous tracing", () =>
            {
                using var h = new Harness(); h.Controller.Settings.PauseAtBreakpoints = false;
                h.StartAutomatic(); h.Debugger.Stop(reason); Equal(false, h.Controller.IsAutomatic); Equal(null, h.Scheduler.Pending);
            });
        }
        Test("Unavailable source stops continuous tracing", () =>
        {
            using var h = new Harness(); h.StartAutomatic(); h.Debugger.Snapshot = new SourceSnapshot(); h.Debugger.Stop();
            Equal(false, h.Controller.IsAutomatic); Equal(2, h.Controller.Records.Count);
        });
        Test("Continue cannot start from an unavailable source location", () =>
        {
            using var h = new Harness(); h.Debugger.Snapshot = new SourceSnapshot(); h.Controller.ContinueTrace();
            Equal(false, h.Controller.IsAutomatic); Equal(null, h.Scheduler.Pending);
        });
        Test("Context changes are not recorded as execution", () =>
        {
            using var h = new Harness(); h.StartAutomatic(); h.Debugger.Stop(StopReason.ContextSwitch);
            Equal(1, h.Controller.Records.Count); Equal(false, h.Controller.IsAutomatic);
        });
        Test("The record limit pauses before another step and bounds storage", () =>
        {
            using var h = new Harness(); h.Controller.Settings.MaxRecords = 3; h.StartAutomatic();
            h.Debugger.Stop(); h.Scheduler.Fire(); h.Debugger.Stop();
            Equal(3, h.Controller.Records.Count); Equal(false, h.Controller.IsAutomatic); Equal(null, h.Scheduler.Pending);
            h.Controller.ContinueTrace(); Equal(false, h.Controller.IsAutomatic);
            for (int i = 0; i < 20; i++) h.Debugger.Stop();
            Equal(3, h.Controller.Records.Count);
        });
        Test("Same-line loop visits are retained", () =>
        {
            using var h = new Harness(); h.StartAutomatic();
            for (int i = 0; i < 50; i++) { h.Debugger.Stop(); h.Scheduler.Fire(); }
            Equal(51, h.Controller.Records.Count); Equal(51L, h.Controller.Records.Last().Sequence);
        });
        Test("External Continue cancels a queued automatic step", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace(); var stale = h.Scheduler.Pending;
            h.Debugger.RunExternally(); stale(); Equal(false, h.Controller.IsAutomatic); Equal(0, h.Debugger.Steps.Count);
        });
        Test("Program end retains trace and invalidates queued work", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace(); var stale = h.Scheduler.Pending;
            h.Debugger.End(); stale(); Equal(1, h.Controller.Records.Count); Equal(false, h.Controller.IsAutomatic);
            Equal(0, h.Debugger.Steps.Count); Equal(false, h.Controller.IsStepOutstanding);
        });
        Test("Step failure cancels continuous tracing", () =>
        {
            using var h = new Harness(); h.Debugger.FailStep = true; h.StartAutomatic();
            Equal(false, h.Controller.IsAutomatic); Equal(false, h.Controller.IsStepOutstanding);
            Check(h.Controller.Status.Contains("failed"));
        });
        Test("Capture failure stops before issuing a debugger command", () =>
        {
            using var h = new Harness(); h.Debugger.FailCapture = true;
            h.Controller.Step(TraceStep.Into); Equal(0, h.Debugger.Steps.Count);
            h.Controller.ContinueTrace(); Equal(false, h.Controller.IsAutomatic);
        });
        Test("Clear resets sequence and invalidates callbacks", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace(); var stale = h.Scheduler.Pending;
            h.Controller.Clear(); stale(); Equal(0, h.Controller.Records.Count); Equal(0, h.Debugger.Steps.Count);
            h.Controller.Step(TraceStep.Into); Equal(1L, h.Controller.Records[0].Sequence);
        });
        Test("Reported debugger errors cancel scheduled tracing", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace(); var stale = h.Scheduler.Pending;
            h.Controller.ReportError("Source file unavailable"); stale();
            Equal(false, h.Controller.IsAutomatic); Equal(0, h.Debugger.Steps.Count);
            Equal("Source file unavailable", h.Controller.Status);
        });
        Test("Dispose detaches events and invalidates callbacks", () =>
        {
            var h = new Harness(); h.Controller.ContinueTrace(); var stale = h.Scheduler.Pending; h.Dispose();
            stale(); h.Debugger.Stop(); Equal(0, h.Debugger.Steps.Count); Equal(1, h.Controller.Records.Count);
        });
        Test("Export preserves JSON text, Unicode, numbers, and empty traces", () =>
        {
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var row = ExportRecord(); var writer = new StringWriter(); TraceExport.WriteJson(writer, new[] { row });
                using var json = JsonDocument.Parse(writer.ToString());
                var entry = json.RootElement.GetProperty("records")[0];
                Equal(row.Location.Source, entry.GetProperty("source").GetString());
                Equal(1.25, entry.GetProperty("elapsedMilliseconds").GetDouble());
                writer = new StringWriter(); TraceExport.WriteJson(writer, Array.Empty<TraceRecord>());
                using var empty = JsonDocument.Parse(writer.ToString()); Equal(0, empty.RootElement.GetProperty("records").GetArrayLength());
            }
            finally { CultureInfo.CurrentCulture = culture; }
        });
        Test("CSV escapes quotes, multiline text, and spreadsheet formulas", () =>
        {
            var writer = new StringWriter(); var row = ExportRecord(); row.Location.Function = "=danger()";
            TraceExport.WriteCsv(writer, new[] { row }); var csv = writer.ToString();
            Check(csv.Contains("\"'=danger()\"")); Check(csv.Contains("\"\"quoted\"\"")); Check(csv.Contains("1.25"));
        });
        Test("Deferred mode failures stop tracing without escaping the callback", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace(); h.Debugger.FailMode = true;
            h.Scheduler.Fire(); Equal(false, h.Controller.IsAutomatic); Equal(false, h.Controller.IsStepOutstanding);
            Equal(DebugMode.Unknown, h.Controller.Mode); Equal(null, h.Scheduler.Pending);
            h.Debugger.FailMode = false; h.Controller.PollDebuggerState();
            Equal(DebugMode.Break, h.Controller.Mode); h.Controller.ContinueTrace(); Check(h.Controller.IsAutomatic);
        });
        Test("Polling debugger failure is contained and UI reads only cached mode", () =>
        {
            using var h = new Harness(); h.Debugger.FailMode = true; h.Controller.PollDebuggerState();
            Equal(DebugMode.Unknown, h.Controller.Mode); Equal(true, h.Controller.RecordingInterrupted);
        });
        Test("Context notification cancels a queued step when the selected thread changes", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace(); var stale = h.Scheduler.Pending;
            h.Debugger.Snapshot = new SourceSnapshot { File = "another.cs", Line = 8, ThreadId = 99 };
            h.Debugger.ChangeContext(); stale(); Equal(false, h.Controller.IsAutomatic); Equal(0, h.Debugger.Steps.Count);
            Equal(1, h.Controller.Records.Count);
        });
        Test("Normal step context notifications preserve automatic tracing", () =>
        {
            using var h = new Harness(); h.StartAutomatic();
            h.Debugger.ChangeContext(); // Still running; this notification belongs to execution.
            h.Debugger.Stop(); var pending = h.Scheduler.Pending;
            h.Debugger.ChangeContext(); // Same completed frame, often reported after a stop.
            Equal(true, h.Controller.IsAutomatic); Equal(pending, h.Scheduler.Pending);
        });
        Test("Queued callback also revalidates context if a notification is missed", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace();
            h.Debugger.Snapshot = new SourceSnapshot { File = "different.cs", Line = 1 };
            h.Scheduler.Fire(); Equal(false, h.Controller.IsAutomatic); Equal(0, h.Debugger.Steps.Count);
        });
        Test("Loading source at the same stop repairs Continue eligibility", () =>
        {
            using var h = new Harness(); h.Debugger.Snapshot = new SourceSnapshot(); h.Debugger.Stop();
            h.Debugger.Snapshot = new SourceSnapshot { File = "loaded.cs", Line = 4 };
            h.Controller.ContinueTrace(); Equal(true, h.Controller.IsAutomatic); Check(h.Scheduler.Pending != null);
        });
        Test("Losing source at a recorded stop blocks Continue", () =>
        {
            using var h = new Harness(); h.Debugger.Stop(); h.Debugger.Snapshot = new SourceSnapshot();
            h.Controller.ContinueTrace(); Equal(false, h.Controller.IsAutomatic); Equal(null, h.Scheduler.Pending);
        });
        Test("Live settings reschedule the delay and change breakpoint behavior", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace(); var stale = h.Scheduler.Pending;
            h.Controller.UpdateSettings(new TraceSettings { DelayMilliseconds = 300, PauseAtBreakpoints = false });
            stale(); Equal(0, h.Debugger.Steps.Count); Equal(300, h.Scheduler.Delay);
            h.Scheduler.Fire(); h.Debugger.Stop(StopReason.Breakpoint); Check(h.Controller.IsAutomatic);
        });
        Test("Lowering the live limit pauses an in-flight trace and requests Break", () =>
        {
            using var h = new Harness(); h.StartAutomatic();
            h.Controller.UpdateSettings(new TraceSettings { MaxRecords = 1 });
            Equal(false, h.Controller.IsAutomatic); Equal(1, h.Debugger.BreakCalls); Equal(1, h.Controller.Records.Count);
        });
        Test("Native stops report omitted records and export the capacity metadata", () =>
        {
            using var h = new Harness(); h.Controller.Settings.MaxRecords = 2;
            h.Debugger.Stop(); h.Debugger.Stop(); h.Debugger.Stop(); h.Debugger.Stop();
            Equal(2L, h.Controller.DroppedStops); Check(h.Controller.RecordingNotice.Contains("INCOMPLETE"));
            var snapshot = h.Controller.CreateExportSnapshot(); var jsonText = new StringWriter();
            TraceExport.WriteJson(jsonText, snapshot); using var json = JsonDocument.Parse(jsonText.ToString());
            Equal(2L, json.RootElement.GetProperty("droppedStops").GetInt64());
            Check(json.RootElement.GetProperty("atCapacity").GetBoolean());
            var csv = new StringWriter(); TraceExport.WriteCsv(csv, snapshot); Check(csv.ToString().Contains("DroppedStops,AtCapacity"));
            h.Controller.Clear(); Equal(0L, h.Controller.DroppedStops); Equal("", h.Controller.RecordingNotice);
        });
        Test("Increasing capacity retains the previous omission warning", () =>
        {
            using var h = new Harness(); h.Controller.Settings.MaxRecords = 1; h.Debugger.Stop(); h.Debugger.Stop();
            h.Controller.UpdateSettings(new TraceSettings { MaxRecords = 10 });
            Equal(false, h.Controller.AtCapacity); Equal(1L, h.Controller.DroppedStops); Check(h.Controller.RecordingNotice.Contains("INCOMPLETE"));
        });
        Test("Clear rejects in-flight steps even when the UI button has not refreshed", () =>
        {
            using var h = new Harness(); h.Controller.Step(TraceStep.Into); h.Controller.Clear();
            Equal(1, h.Controller.Records.Count); Check(h.Controller.IsStepOutstanding);
            h.Debugger.Stop(); Equal(2, h.Controller.Records.Count); h.Controller.Clear(); Equal(0, h.Controller.Records.Count);
        });
        Test("Missing completion notifications recover without automatically retrying", () =>
        {
            using var h = new Harness(DebugMode.Design); h.Debugger.NoOpStep = true;
            h.Controller.Step(TraceStep.Into); h.Time = 14999; h.Controller.PollDebuggerState(); Check(h.Controller.IsStepOutstanding);
            h.Time = 15000; h.Controller.PollDebuggerState(); Equal(false, h.Controller.IsStepOutstanding);
            Equal(false, h.Controller.IsAutomatic); Equal(true, h.Controller.RecordingInterrupted); Equal(1, h.Debugger.Steps.Count);
            h.Controller.Step(TraceStep.Into); Equal(2, h.Debugger.Steps.Count);
        });
        Test("Long running steps are not timed out and stop recovery gets a grace period", () =>
        {
            using var h = new Harness(); h.Controller.Step(TraceStep.Into);
            h.Time = 90000; h.Controller.PollDebuggerState(); Check(h.Controller.IsStepOutstanding);
            h.Debugger.Mode = DebugMode.Break; h.Time = 90500; h.Controller.PollDebuggerState(); Check(h.Controller.IsStepOutstanding);
            h.Time = 105000; h.Controller.PollDebuggerState(); Equal(false, h.Controller.IsStepOutstanding);
            Equal("Recovery", h.Controller.Records.Last().Operation);
        });
        Test("An earlier file operation cannot cancel a new trace or overwrite a newer message", () =>
        {
            using var h = new Harness(); var status = new OperationStatus(); long export = status.Begin("Exporting A");
            h.Controller.ContinueTrace(); status.Complete(export, "Export A failed"); Check(h.Controller.IsAutomatic);
            long newer = status.Begin("Exporting B"); status.Complete(export, "Old completion"); Equal("Exporting B", status.Message);
            status.Complete(newer, "B saved"); Equal("B saved", status.Message); Check(h.Controller.IsAutomatic);
        });
        Test("Source completion updates its own row without mutating an export snapshot", () =>
        {
            using var h = new Harness(); h.Debugger.Stop(); var record = h.Controller.Records[0];
            var snapshot = h.Controller.CreateExportSnapshot();
            Check(h.Controller.TrySetSource(record, "Loaded();")); Equal("Loaded();", record.Location.Source);
            Equal("Loop();", snapshot.Records[0].Location.Source);
            h.Controller.Clear(); h.Debugger.Stop(); Equal(false, h.Controller.TrySetSource(record, "Old();"));
            Equal("Loop();", h.Controller.Records[0].Location.Source);
        });
        Test("Disposed traces reject late source completions", () =>
        {
            var h = new Harness(); h.Debugger.Stop(); var row = h.Controller.Records[0]; h.Dispose();
            Equal(false, h.Controller.TrySetSource(row, "late"));
        });
        Test("Captured locations do not alias mutable debugger snapshots", () =>
        {
            using var h = new Harness(); h.Debugger.Stop(); h.Debugger.Snapshot.Line = 999;
            Equal(12, h.Controller.Records[0].Location.Line);
        });
        Test("Export frame position is named frameIndex and source availability is explicit", () =>
        {
            var row = ExportRecord(); row.Location.FrameIndex = 3; row.Location.SourceState = "Pending";
            var writer = new StringWriter(); TraceExport.WriteJson(writer, new[] { row });
            using var json = JsonDocument.Parse(writer.ToString()); var entry = json.RootElement.GetProperty("records")[0];
            Equal(2, json.RootElement.GetProperty("formatVersion").GetInt32()); Equal(3, entry.GetProperty("frameIndex").GetInt32());
            Equal(false, entry.TryGetProperty("depth", out _)); Equal("Pending", entry.GetProperty("sourceState").GetString());
        });
        Test("Successful atomic export replaces an existing destination", () => WithFiles(directory =>
        {
            string target = Path.Combine(directory, "trace.json"); File.WriteAllText(target, "original");
            AtomicTraceFile.Write(target, false, writer => writer.Write("replacement"));
            Equal("replacement", File.ReadAllText(target)); Equal(1, Directory.GetFiles(directory).Length);
        }));
        Test("Failed atomic export preserves the previous destination and removes temporary data", () => WithFiles(directory =>
        {
            string target = Path.Combine(directory, "trace.json"); File.WriteAllText(target, "original");
            try { AtomicTraceFile.Write(target, false, writer => { writer.Write("partial"); throw new IOException("disk failure"); }); throw new Exception("Expected failure"); }
            catch (IOException) { }
            Equal("original", File.ReadAllText(target)); Equal(1, Directory.GetFiles(directory).Length);
        }));
        Test("Canceled export preserves the destination", () => WithFiles(directory =>
        {
            string target = Path.Combine(directory, "trace.json"); File.WriteAllText(target, "original"); using var cancel = new CancellationTokenSource();
            try { AtomicTraceFile.Write(target, false, writer => { writer.Write("new"); cancel.Cancel(); }, cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Equal("original", File.ReadAllText(target)); Equal(1, Directory.GetFiles(directory).Length);
        }));
        Test("Atomic export preserves a destination created concurrently", () => WithFiles(directory =>
        {
            string target = Path.Combine(directory, "trace.json");
            try { AtomicTraceFile.Write(target, false, writer => { writer.Write("new"); File.WriteAllText(target, "concurrent"); }); throw new Exception("Expected conflict"); }
            catch (IOException) { }
            Equal("concurrent", File.ReadAllText(target)); Equal(1, Directory.GetFiles(directory).Length);
        }));
        Test("Source I/O runs off the caller thread", () =>
        {
            int caller = Thread.CurrentThread.ManagedThreadId; int worker = caller;
            using var reader = new SourceTextReader(readLine: (file, line) => { worker = Thread.CurrentThread.ManagedThreadId; return "source"; });
            var task = reader.ReadAsync("test.cs", 1); Check(task.Wait(2000)); Equal("source", task.Result); Check(worker != caller);
        });
        Test("Unreadable source produces an unavailable snippet without throwing", () =>
        {
            using var reader = new SourceTextReader(readLine: (file, line) => throw new IOException("offline source"));
            var task = reader.ReadAsync("offline.cs", 1); Check(task.Wait(2000)); Equal(null, task.Result);
        });
        Test("A queued step cannot relaunch a session if its end event was missed", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace(); h.Debugger.Mode = DebugMode.Design;
            h.Scheduler.Fire(); Equal(0, h.Debugger.Steps.Count); Equal(false, h.Controller.IsAutomatic);
        });
        Test("Unknown debugger mode cancels pending automatic work", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace(); h.Debugger.Mode = DebugMode.Unknown;
            h.Scheduler.Fire(); Equal(false, h.Controller.IsAutomatic); Equal(null, h.Scheduler.Pending);
        });
        Test("A blocked source reader has a bounded queue and nonblocking disposal", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var reader = new SourceTextReader(1, (file, line) => { entered.Set(); release.Wait(5000); return "source"; });
            var active = reader.ReadAsync("slow.cs", 1); Check(entered.Wait(2000));
            var queued = reader.ReadAsync("next.cs", 1); var overflow = reader.ReadAsync("overflow.cs", 1);
            Check(overflow.IsCompleted); Equal(null, overflow.Result);
            reader.Dispose(); Check(active.IsCanceled); Check(queued.IsCanceled); release.Set();
        });
        Test("Source navigation is deferred and superseded callbacks cannot run", () =>
        {
            using var h = new FollowHarness();
            h.Add("a.cs"); var stale = h.Scheduler.Pending; var latest = h.Add("b.cs");
            Equal(0, h.Displays); stale(); Equal(0, h.Displays);
            h.Scheduler.Fire(); Equal(latest, h.Follower.SelectedRecord); Equal("b.cs", h.Shown.File);
        });
        Test("Grid and editor follow the same newest matching record", () =>
        {
            using var h = new FollowHarness(); h.Follower.SetFilter("matching");
            var matching = h.Add("matching.cs"); h.Add("other.cs"); h.Scheduler.Fire();
            Equal(matching, h.Follower.SelectedRecord); Equal("matching.cs", h.Shown.File);
        });
        Test("A filter with no matches clears both target and highlight", () =>
        {
            using var h = new FollowHarness(); h.Add("a.cs"); h.Scheduler.Fire();
            h.Follower.SetFilter("nothing"); Equal(null, h.Follower.SelectedRecord); Equal(null, h.Shown); Equal(null, h.Scheduler.Pending);
        });
        Test("Source completion can change the matching target without adding records", () =>
        {
            using var h = new FollowHarness(); var record = h.Add("pending.cs"); h.Follower.SetFilter("needle");
            Equal(null, h.Follower.SelectedRecord); record.Location.Source = "needle";
            h.Follower.Refresh(); h.Scheduler.Fire(); Equal(record, h.Follower.SelectedRecord); Equal("pending.cs", h.Shown.File);
        });
        Test("Clear rejects a stale visible row before the UI refresh", () =>
        {
            using var h = new FollowHarness(); var staleRow = h.Add("cleared.cs"); h.Scheduler.Fire();
            h.Records.Clear(); h.Follower.Refresh();
            Equal(false, h.Follower.Select(staleRow, true)); Equal(null, h.Shown); Equal(null, h.Scheduler.Pending);
        });
        Test("A stale row cannot cancel a newer history request with the same sequence number", () =>
        {
            using var h = new FollowHarness(); var staleRow = h.Add("old.cs");
            h.Records.Clear(); h.Follower.Refresh(); var current = h.Add("new.cs");
            Equal(false, h.Follower.Select(staleRow, true)); Check(h.Follower.Following);
            h.Scheduler.Fire(); Equal(current, h.Follower.SelectedRecord); Equal("new.cs", h.Shown.File);
        });
        Test("Explicit open always pauses following and preserves editor focus intent", () =>
        {
            using var h = new FollowHarness(); var selected = h.Add("selected.cs");
            Check(h.Follower.Select(selected, true)); h.Add("new-stop.cs"); h.Scheduler.Fire();
            Equal(false, h.Follower.Following); Equal(selected, h.Follower.SelectedRecord);
            Equal("selected.cs", h.Shown.File); Equal(true, h.Activated);
        });
        Test("Re-enabling follow chooses the latest matching row, not the latest overall", () =>
        {
            using var h = new FollowHarness(); h.Follower.SetFilter("match");
            var first = h.Add("match-first.cs"); h.Follower.Select(first, false); h.Scheduler.Fire();
            var latest = h.Add("match-last.cs"); h.Add("excluded.cs");
            h.Follower.SetFollowing(true); h.Scheduler.Fire();
            Equal(latest, h.Follower.SelectedRecord); Equal("match-last.cs", h.Shown.File);
        });
        Test("Failed navigation clears the old highlight and reports independently", () =>
        {
            using var h = new FollowHarness(); h.Add("old.cs"); h.Scheduler.Fire();
            h.BeforeCommit = request => throw new IOException("source missing");
            h.Add("missing.cs"); Equal(null, h.Shown); h.Scheduler.Fire();
            Equal(null, h.Shown); Equal(1, h.Errors); Equal("source missing", h.Error);
        });
        Test("A Clear reentered during document opening prevents highlight and focus commit", () =>
        {
            using var h = new FollowHarness(); h.Add("old.cs");
            h.BeforeCommit = request => { h.Records.Clear(); h.Follower.Refresh(); };
            h.Scheduler.Fire(); Equal(null, h.Shown); Equal(null, h.Follower.SelectedRecord); Equal(0, h.Displays);
        });
        Test("A stale navigation failure cannot erase a newer reentrant selection", () =>
        {
            using var h = new FollowHarness(); var old = h.Add("old.cs"); var newer = h.Add("new.cs");
            h.Follower.Select(old, true);
            h.BeforeCommit = request =>
            {
                if (request.Location.File != "old.cs") return;
                h.Follower.Select(newer, true); h.Scheduler.Fire();
                throw new IOException("obsolete failure");
            };
            h.Scheduler.Fire(); Equal("new.cs", h.Shown.File); Equal(0, h.Errors); Equal(true, h.Activated);
        });
        Test("A filter change invalidates an active request even when its row still matches", () =>
        {
            using var h = new FollowHarness(); h.Add("alpha.cs");
            h.BeforeCommit = request =>
            {
                if (h.Follower.Filter.Length != 0) return;
                h.Follower.SetFilter("alpha"); Equal(false, request.IsCurrent);
            };
            h.Scheduler.Fire(); Equal(null, h.Shown); h.Scheduler.Fire(); Equal("alpha.cs", h.Shown.File);
        });
        Test("A target selected reentrantly by a notification keeps its newer queued request", () =>
        {
            using var h = new FollowHarness(); var a = h.Add("a.cs"); var b = h.Add("b.cs");
            h.Follower.Changed += (_, _) => { if (ReferenceEquals(h.Follower.SelectedRecord, a)) h.Follower.Select(b, true); };
            h.Follower.Select(a, false); h.Scheduler.Fire(); Equal("b.cs", h.Shown.File); Equal(true, h.Activated);
        });
        Test("Disposal during navigation invalidates its commit and removes the marker", () =>
        {
            using var h = new FollowHarness(); h.Add("old.cs");
            h.BeforeCommit = request => h.Follower.Dispose();
            h.Scheduler.Fire(); Equal(0, h.Displays); Equal(null, h.Shown);
        });
        Test("Source snippet updates preserve an explicit pending focus request", () =>
        {
            using var h = new FollowHarness(); var row = h.Add("a.cs"); h.Follower.Select(row, true);
            row.Location.Source = "resolved source"; h.Follower.Refresh(); h.Scheduler.Fire(); Equal(true, h.Activated);
        });
        Test("Changed location metadata invalidates an old navigation snapshot", () =>
        {
            using var h = new FollowHarness(); var row = h.Add("a.cs");
            row.Location.Line = 25; h.Scheduler.Fire(); Equal(null, h.Shown);
            h.Follower.Refresh(); h.Scheduler.Fire(); Equal(25, h.Shown.Line);
        });
        Test("Filter matching is shared, trimmed, and case-insensitive", () =>
        {
            using var h = new FollowHarness(); var row = h.Add("C:\\Code\\Example.cs");
            h.Follower.SetFilter("  EXAMPLE  "); h.Scheduler.Fire();
            Check(SourceFollower.Matches(row, h.Follower.Filter)); Equal(row, h.Follower.SelectedRecord);
        });
        Test("Eviction from the visible window clears a manual selection without resuming following", () =>
        {
            using var h = new FollowHarness(); var first = h.Add("first.cs"); h.Follower.Select(first, false); h.Scheduler.Fire();
            for (int i = 0; i < SourceFollower.VisibleRecordLimit; i++)
                h.Records.Add(new TraceRecord { Sequence = h.Records.Count + 1, Location = new SourceSnapshot { File = "more.cs", Line = 1 } });
            h.Follower.Refresh(); Equal(null, h.Follower.SelectedRecord); Equal(null, h.Shown); Equal(false, h.Follower.Following);
        });

        Test("Pause reentered during queued capture prevents the debugger command", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace();
            h.Debugger.BeforeCapture = h.Controller.Pause;
            h.Scheduler.Fire();
            Equal(0, h.Debugger.Steps.Count);
        });
        Test("Disposal reentered during queued capture prevents the debugger command", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace();
            h.Debugger.BeforeCapture = h.Controller.Dispose;
            h.Scheduler.Fire();
            Equal(0, h.Debugger.Steps.Count);
        });
        Test("Transient mode failure preserves the in-flight command guard", () =>
        {
            using var h = new Harness(); h.Debugger.NoOpStep = true;
            h.Controller.Step(TraceStep.Into);
            h.Debugger.FailMode = true; h.Controller.PollDebuggerState();
            h.Debugger.FailMode = false; h.Controller.PollDebuggerState();
            h.Controller.Step(TraceStep.Over);
            Equal(1, h.Debugger.Steps.Count);
        });
        Test("Transient mode failure does not bypass Clear's in-flight protection", () =>
        {
            using var h = new Harness(); h.Debugger.NoOpStep = true;
            h.Controller.Step(TraceStep.Into);
            h.Debugger.FailMode = true; h.Controller.PollDebuggerState();
            h.Debugger.FailMode = false; h.Controller.Clear();
            Equal(1, h.Controller.Records.Count);
        });
        Test("Mode failure followed by a missing stop event still records recovery", () =>
        {
            using var h = new Harness(); h.StartAutomatic();
            h.Debugger.FailMode = true; h.Controller.PollDebuggerState();
            h.Debugger.Mode = DebugMode.Break; h.Debugger.FailMode = false;
            h.Debugger.Snapshot.Line = 20; h.Time = 30000; h.Controller.PollDebuggerState();
            Equal(2, h.Controller.Records.Count);
        });
        Test("Disabling follow while navigation is pending retains a visible selection marker", () =>
        {
            using var h = new FollowHarness(); var row = h.Add("selected.cs");
            h.Follower.SetFollowing(false); h.Follower.Refresh();
            if (h.Scheduler.Pending != null) h.Scheduler.Fire();
            Equal(row, h.Follower.SelectedRecord);
            Check(h.Shown != null);
        });

        foreach (bool automatic in new[] { false, true })
        foreach (bool dispose in new[] { false, true })
        {
            Test((automatic ? "Continue" : "Manual step") + " aborts when initial capture reenters " + (dispose ? "Dispose" : "Pause"), () =>
            {
                using var h = new Harness();
                h.Debugger.BeforeCapture = dispose ? h.Controller.Dispose : h.Controller.Pause;
                if (automatic) h.Controller.ContinueTrace(); else h.Controller.Step(TraceStep.Into);
                Equal(0, h.Debugger.Steps.Count); Equal(0, h.Controller.Records.Count);
                Equal(false, h.Controller.IsStepOutstanding); Equal(null, h.Scheduler.Pending);
            });
        }
        Test("Cancellation during a queued mode read prevents issuing a command", () =>
        {
            using var h = new Harness(); h.Controller.ContinueTrace();
            h.Debugger.BeforeModeRead = h.Controller.Pause;
            h.Scheduler.Fire(); Equal(0, h.Debugger.Steps.Count); Equal(false, h.Controller.IsStepOutstanding);
        });
        Test("Pause during command status notification leaves no phantom outstanding step", () =>
        {
            using var h = new Harness(); bool cancel = true;
            h.Controller.Changed += (_, _) =>
            {
                if (!cancel || !h.Controller.Status.StartsWith("Waiting for Trace")) return;
                cancel = false; h.Controller.Pause();
            };
            h.Controller.Step(TraceStep.Into);
            Equal(0, h.Debugger.Steps.Count); Equal(false, h.Controller.IsStepOutstanding);
            h.Controller.Clear(); Equal(0, h.Controller.Records.Count);
        });
        Test("Pause during queue notification prevents scheduling canceled work", () =>
        {
            using var h = new Harness();
            h.Controller.Changed += (_, _) => { if (h.Controller.IsAutomatic) h.Controller.Pause(); };
            h.Controller.ContinueTrace();
            Equal(null, h.Scheduler.Pending); Equal(0, h.Debugger.Steps.Count);
        });
        Test("A newer command issued during capture supersedes the earlier manual request", () =>
        {
            using var h = new Harness(); h.Debugger.BeforeCapture = () => h.Controller.Step(TraceStep.Over);
            h.Controller.Step(TraceStep.Into);
            Equal(1, h.Debugger.Steps.Count); Equal(TraceStep.Over, h.Debugger.Steps[0]);
            Check(h.Controller.IsStepOutstanding);
        });
        Test("Clear during initial record notification cancels the pending manual request", () =>
        {
            using var h = new Harness(); h.Controller.RecordAdded += (_, _) => h.Controller.Clear();
            h.Controller.Step(TraceStep.Into);
            Equal(0, h.Debugger.Steps.Count); Equal(0, h.Controller.Records.Count);
        });
        Test("A real completion after a mode failure settles the original command", () =>
        {
            using var h = new Harness(); h.Controller.Step(TraceStep.Over);
            h.Debugger.FailMode = true; h.Controller.PollDebuggerState(); Check(h.Controller.IsStepOutstanding);
            h.Debugger.FailMode = false; h.Debugger.Stop();
            Equal(false, h.Controller.IsStepOutstanding); Equal("Trace Over", h.Controller.Records.Last().Operation);
            Equal(2, h.Controller.Records.Count); Equal(null, h.Scheduler.Pending);
        });
        Test("A running command stays guarded after recovery from a mode-read failure", () =>
        {
            using var h = new Harness(); h.Controller.Step(TraceStep.Into);
            h.Debugger.FailMode = true; h.Controller.PollDebuggerState();
            h.Debugger.FailMode = false; h.Time = 90000; h.Controller.PollDebuggerState();
            Check(h.Controller.IsStepOutstanding); Equal(1, h.Debugger.Steps.Count);
        });
        Test("Freezing follow preserves pending focus intent and ignores later records", () =>
        {
            using var h = new FollowHarness(); var selected = h.Add("selected.cs");
            h.Follower.Select(selected, true); h.Follower.SetFollowing(false);
            h.Add("later.cs"); h.Scheduler.Fire();
            Equal(selected, h.Follower.SelectedRecord); Equal("selected.cs", h.Shown.File); Check(h.Activated);
        });

        Console.WriteLine($"\n{passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static TraceRecord ExportRecord() => new TraceRecord
    {
        Sequence = 1, TimestampUtc = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc), ElapsedMilliseconds = 1.25,
        Operation = "Trace Into", Reason = "Step", Location = new SourceSnapshot { File = @"C:\code\demo.cs", Line = 10, Source = "\"quoted\", line\n\tλ 漢字 🚀\\end" }
    };
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void WithFiles(Action<string> action)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "audit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }

    private sealed class FollowHarness : IDisposable
    {
        public readonly List<TraceRecord> Records = new List<TraceRecord>();
        public readonly FakeScheduler Scheduler = new FakeScheduler();
        public readonly SourceFollower Follower;
        public SourceSnapshot Shown;
        public bool Activated;
        public int Displays, Errors;
        public string Error;
        public Action<SourceNavigationRequest> BeforeCommit;
        public FollowHarness()
        {
            Follower = new SourceFollower(Scheduler, () => Records, request =>
            {
                BeforeCommit?.Invoke(request);
                request.TryCommit(() => { Shown = request.Location; Activated = request.ActivateEditor; Displays++; });
            }, () => Shown = null, ex => { Errors++; Error = ex.Message; });
        }
        public TraceRecord Add(string file)
        {
            var record = new TraceRecord { Sequence = Records.Count + 1, Location = new SourceSnapshot { File = file, Line = 1 } };
            Records.Add(record); Follower.Refresh(); return record;
        }
        public void Dispose() { Follower.Dispose(); }
    }

    private sealed class Harness : IDisposable
    {
        public readonly FakeDebugger Debugger;
        public readonly FakeScheduler Scheduler = new FakeScheduler();
        public readonly TraceController Controller;
        public long Time;
        public Harness(DebugMode mode = DebugMode.Break) { Debugger = new FakeDebugger { Mode = mode }; Controller = new TraceController(Debugger, Scheduler, new TraceSettings(), () => Time); }
        public void StartAutomatic() { Controller.ContinueTrace(); Scheduler.Fire(); }
        public void Dispose() => Controller.Dispose();
    }
    private sealed class FakeScheduler : ITraceScheduler
    {
        public Action Pending;
        public int Delay;
        public void Schedule(Action callback, int delayMilliseconds) { Pending = callback; Delay = delayMilliseconds; }
        public void Cancel() { Pending = null; }
        public void Fire() { var callback = Pending ?? throw new Exception("No step queued."); Pending = null; callback(); }
        public void Dispose() => Cancel();
    }
    private sealed class FakeDebugger : ITraceDebugger
    {
        private DebugMode mode;
        public Action BeforeModeRead;
        public DebugMode Mode
        {
            get
            {
                var callback = BeforeModeRead; BeforeModeRead = null; callback?.Invoke();
                return FailMode ? throw new COMException("mode unavailable") : mode;
            }
            set => mode = value;
        }
        public readonly List<TraceStep> Steps = new List<TraceStep>();
        public SourceSnapshot Snapshot = new SourceSnapshot { File = "test.cs", Line = 12, Function = "Test.Main", Source = "Loop();", ThreadId = 5 };
        public int BreakCalls;
        public bool FailStep, FailCapture, FailMode, NoOpStep;
        public event EventHandler<DebugStopEventArgs> Stopped;
        public event EventHandler Running;
        public event EventHandler Ended;
        public event EventHandler ContextChanged;
        public Action BeforeCapture;
        public SourceSnapshot Capture()
        {
            var callback = BeforeCapture; BeforeCapture = null; callback?.Invoke();
            return FailCapture ? throw new Exception("capture unavailable") : Snapshot;
        }
        public void Step(TraceStep step) { if (FailStep) throw new Exception("step unavailable"); Steps.Add(step); if (!NoOpStep) RunExternally(); }
        public void ChangeContext() { ContextChanged?.Invoke(this, EventArgs.Empty); }
        public void Break() { BreakCalls++; }
        public void Stop(StopReason reason = StopReason.Step) { Mode = DebugMode.Break; Stopped?.Invoke(this, new DebugStopEventArgs(reason, reason.ToString())); }
        public void RunExternally() { Mode = DebugMode.Running; Running?.Invoke(this, EventArgs.Empty); }
        public void End() { Mode = DebugMode.Design; Ended?.Invoke(this, EventArgs.Empty); }
    }
}
