using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE90a;

internal static class Program
{
    private const string CommandSet = "{c5797592-4075-432d-881e-bd36aa9bdd94}";
    private static DTE dte;
    private static DebuggerEvents debugEvents;
    private static int stopCount;
    private static readonly HashSet<string> functions = new HashSet<string>();
    private static readonly HashSet<string> reportedInstances = new HashSet<string>();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: SourceTrace.Smoke <isolated devenv PID> <TraceDemo.slnx> [diagnostics-directory]"); return 2; }
        int pid = int.Parse(args[0]);
        // Bounds the entire helper, including synchronous COM calls outside the polling loops.
        using var deadline = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("SMOKE TIMEOUT: the integration helper exceeded its four-minute deadline.");
            Environment.Exit(124);
        }, null, TimeSpan.FromMinutes(4), Timeout.InfiniteTimeSpan);
        MessageFilter.Register();
        try
        {
            Wait(() => (dte = FindDte(pid)) != null, "connect to isolated Visual Studio", 120);
            dte.SuppressUI = true;
            if (args.Length > 3 && args[3] == "--visible")
            {
                dte.MainWindow.Visible = true;
                dte.MainWindow.WindowState = vsWindowState.vsWindowStateMaximize;
                dte.MainWindow.Activate();
            }
            Console.WriteLine("Connected to Visual Studio " + dte.Version + " (PID " + pid + ").");
            // The dedicated instance is launched with this solution. Do not race its startup Open call.
            Wait(() => string.Equals(dte.Solution.FullName, Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase), "open demo solution", 60);
            Wait(() => dte.Solution.Projects.Count > 0, "load demo solution", 60);
            dte.Solution.SolutionBuild.SolutionConfigurations.Item("Release").Activate();
            dte.Solution.SolutionBuild.Build(false);
            Wait(() => dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateDone, "build demo", 60);
            if (dte.Solution.SolutionBuild.LastBuildInfo != 0) throw new Exception("Demo build failed.");
            InspectPackageRegistration();
            Wait(() => { Raise(0x0100); return true; }, "load SourceTrace tool window", 60);
            bool foundWindow = false;
            foreach (Window window in dte.Windows) if (window.Caption == "SourceTrace") foundWindow = true;
            if (!foundWindow) throw new Exception("SourceTrace tool window was not created: " + dte.StatusBar.Text);
            Console.WriteLine("PASS SourceTrace package, menu command, and tool window loaded.");
            debugEvents = dte.Events.DebuggerEvents;
            debugEvents.OnEnterBreakMode += OnBreak;
            Step(0x0101);
            Console.WriteLine("PASS Trace Into launches debugging at " + Frame().FileName + ":" + Frame().LineNumber);
            VerifyEditorLocation();
            CaptureVisualState(args, "entry");
            for (int i = 0; i < 20 && Frame().LineNumber != 13; i++) Step(0x0102);
            if (Frame().LineNumber != 13) throw new Exception("Could not reach the Square call.");
            Step(0x0101);
            if (!Frame().FunctionName.Contains("Square")) throw new Exception("Trace Into did not enter Square.");
            Console.WriteLine("PASS Trace Into enters Square.");
            VerifyEditorLocation();
            Step(0x0103);
            if (!Frame().FunctionName.Contains("Main")) throw new Exception("Trace Out did not return to Main.");
            Console.WriteLine("PASS Trace Out returns to Main.");
            VerifyEditorLocation();
            CaptureVisualState(args, "statement");
            for (int i = 0; i < 20 && Frame().LineNumber != 13; i++) Step(0x0102);
            Step(0x0102);
            if (!Frame().FunctionName.Contains("Main")) throw new Exception("Trace Over entered Square.");
            Console.WriteLine("PASS Trace Over remains in Main.");
            VerifyEditorLocation();
            Raise(0x0104);
            Raise(0x0105);
            Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, "pause continuous tracing", 30);
            int pausedCount = stopCount;
            var pauseWatch = Stopwatch.StartNew();
            while (pauseWatch.ElapsedMilliseconds < 400) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode || stopCount != pausedCount)
                throw new Exception("Pause did not keep automatic tracing stopped.");
            Console.WriteLine("PASS Pause prevents further automatic steps.");
            int before = stopCount;
            Raise(0x0104);
            Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, "continuous trace to program exit", 120);
            if (stopCount <= before + 5 || !AnyFunction("Factorial")) throw new Exception("Continuous trace did not follow nested source calls.");
            Console.WriteLine("PASS Continue Trace followed nested calls to program exit: " + (stopCount - before) + " source stops.");
            CaptureVisualState(args, "finished");
            if (args.Length > 2)
            {
                foreach (Window window in dte.Windows)
                    if (window.Caption == "SourceTrace") { window.Activate(); dte.ExecuteCommand("Window.CloseToolWindow"); break; }
                int prior = stopCount;
                dte.Debugger.StepInto(false);
                Wait(() => stopCount > prior && dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, "check an explicitly closed SourceTrace panel stays closed", 45);
                var settle = Stopwatch.StartNew();
                while (settle.ElapsedMilliseconds < 300) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                dynamic probe = dte.GetObject("SourceTrace.Test");
                string state = (string)probe.Inspect("");
                Console.WriteLine("After close and native step: " + state);
                if (!state.Contains("Panel requested=False") || state.Contains("Panel.IsVisible=True"))
                    throw new Exception("The explicitly closed SourceTrace panel reopened.");
                Console.WriteLine("PASS Explicitly closing SourceTrace is respected.");
                Raise(0x0100);
                CaptureVisualState(args, "reopened");
                probe.Filter("no-matching-trace-row");
                string filtered = (string)probe.Inspect("");
                if (!TrailLines(filtered).Contains(10) || !TrailLines(filtered).Contains(24))
                    throw new Exception("Filtering erased the visited-line trail.");
                probe.Filter("");
                probe.ClearTrace();
                var clearSettle = Stopwatch.StartNew();
                while (clearSettle.ElapsedMilliseconds < 600) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                string cleared = (string)probe.Inspect(Path.Combine(Path.GetFullPath(args[2]), "cleared"));
                Console.WriteLine(cleared);
                if (!cleared.Contains("Trail unique lines=0") || TrailLines(cleared).Count != 0 || !cleared.Contains("Native marker=False") || !cleared.Contains("Queried marker spans=0"))
                    throw new Exception("Clear left source highlights behind.");
                Console.WriteLine("PASS Filtering preserves the trail and Clear removes all extension highlights.");
            }
            Console.WriteLine("SMOKE PASSED. Total debugger stops: " + stopCount);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("SMOKE FAILED: " + ex); return 1; }
        finally
        {
            try
            {
                if (debugEvents != null) debugEvents.OnEnterBreakMode -= OnBreak;
                if (dte != null) { if (dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode) dte.Debugger.Stop(false); dte.Solution.Close(false); dte.Quit(); }
            }
            catch (Exception ex) { Console.Error.WriteLine("Test instance cleanup: " + ex.Message); }
            MessageFilter.Revoke();
        }
    }

    private static bool AnyFunction(string name) { foreach (var function in functions) if (function.Contains(name)) return true; return false; }
    private static HashSet<int> TrailLines(string report)
    {
        var result = new HashSet<int>();
        foreach (string line in report.Split('\n'))
            if (line.StartsWith("Trail rendered lines=", StringComparison.Ordinal))
                foreach (string number in line.Substring("Trail rendered lines=".Length).Trim().Split(','))
                    if (int.TryParse(number, out int value)) result.Add(value);
        return result;
    }
    private static void CaptureVisualState(string[] args, string phase)
    {
        if (args.Length <= 2) return;
        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 600) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
        dynamic probe = dte.GetObject("SourceTrace.Test");
        string report = (string)probe.Inspect(Path.Combine(Path.GetFullPath(args[2]), phase));
        Console.WriteLine(report);
        if ((phase == "statement" || phase == "finished" || phase == "reopened") &&
            (!TrailLines(report).Contains(10) || !TrailLines(report).Contains(24)))
            throw new Exception("Earlier source lines did not retain their highlights during " + phase);
        if (phase == "finished" && TrailLines(report).Contains(18))
            throw new Exception("The unvisited exception branch was highlighted.");
        if (args.Length > 3 && args[3] == "--visible" &&
            (!report.Contains("Panel.IsVisible=True") || !report.Contains("Editor.IsVisible=True") || !report.Contains("Native marker=True") ||
             !report.Contains("Selected row visible=True") || !report.Contains("Editor.ViewportLeft=0")))
            throw new Exception("Visual state check failed during " + phase);
    }
    private static void VerifyEditorLocation()
    {
        string file = Frame().FileName;
        int line = (int)Frame().LineNumber;
        Wait(() => dte.ActiveDocument != null && string.Equals(dte.ActiveDocument.FullName, file, StringComparison.OrdinalIgnoreCase) &&
            dte.ActiveDocument.Selection is TextSelection selection && selection.ActivePoint.Line == line,
            "follow the traced source line in the editor", 15);
        Console.WriteLine("PASS Editor follows " + Path.GetFileName(file) + ":" + line);
    }
    private static void InspectPackageRegistration()
    {
        var services = (Microsoft.VisualStudio.OLE.Interop.IServiceProvider)dte;
        var serviceId = typeof(Microsoft.VisualStudio.Shell.Interop.SVsShell).GUID;
        var interfaceId = typeof(Microsoft.VisualStudio.Shell.Interop.IVsShell).GUID;
        Marshal.ThrowExceptionForHR(services.QueryService(ref serviceId, ref interfaceId, out var pointer));
        try
        {
            var shell = (Microsoft.VisualStudio.Shell.Interop.IVsShell)Marshal.GetObjectForIUnknown(pointer);
            var packageId = new Guid("dc93d119-7e8b-4f22-8a19-8db4715101e1");
            Marshal.ThrowExceptionForHR(shell.IsPackageInstalled(ref packageId, out int installed));
            Console.WriteLine("SourceTrace package registered: " + installed);
            if (installed == 0) throw new Exception("Visual Studio did not register SourceTrace's package from the VSIX.");
        }
        finally { Marshal.Release(pointer); }
    }
    private static StackFrame2 Frame() => (StackFrame2)dte.Debugger.CurrentStackFrame;
    private static void Step(int id)
    {
        int before = stopCount;
        Raise(id);
        Wait(() => stopCount > before && dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, "complete source step " + id, 45);
    }
    private static void Raise(int id) { object input = null, output = null; dte.Commands.Raise(CommandSet, id, ref input, ref output); }
    private static void OnBreak(dbgEventReason reason, ref dbgExecutionAction action)
    {
        stopCount++;
    }
    private static void Wait(Func<bool> predicate, string operation, int seconds)
    {
        Console.WriteLine("Waiting to " + operation + "...");
        var watch = Stopwatch.StartNew();
        Exception last = null;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            Application.DoEvents();
            try
            {
                if (dte != null && dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode)
                    functions.Add(Frame().FunctionName);
                if (predicate()) return;
            }
            catch (COMException ex) { last = ex; }
            catch (ArgumentException ex) when (ex.HResult == unchecked((int)0x80070057)) { last = ex; }
            System.Threading.Thread.Sleep(50);
        }
        throw new TimeoutException(operation + " timed out." + (last == null ? "" : " " + last.Message));
    }

    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
    private static DTE FindDte(int pid)
    {
        GetRunningObjectTable(0, out var table);
        table.EnumRunning(out var enumerator);
        CreateBindCtx(0, out var context);
        try
        {
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                try
                {
                    monikers[0].GetDisplayName(context, null, out var name);
                    if (name.IndexOf("VisualStudio.DTE", StringComparison.OrdinalIgnoreCase) >= 0 && reportedInstances.Add(name))
                        Console.WriteLine("Discovered automation instance: " + name);
                    if (name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal) && name.EndsWith(":" + pid, StringComparison.Ordinal))
                    { table.GetObject(monikers[0], out var item); return (DTE)item; }
                }
                finally { Marshal.ReleaseComObject(monikers[0]); }
            }
            return null;
        }
        finally { Marshal.ReleaseComObject(context); Marshal.ReleaseComObject(enumerator); Marshal.ReleaseComObject(table); }
    }

    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int type, IntPtr caller, int ticks, IntPtr info);
        [PreserveSig] int RetryRejectedCall(IntPtr callee, int ticks, int rejectType);
        [PreserveSig] int MessagePending(IntPtr callee, int ticks, int pendingType);
    }
    private sealed class MessageFilter : IOleMessageFilter
    {
        private static IOleMessageFilter previous;
        [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IOleMessageFilter filter, out IOleMessageFilter oldFilter);
        public static void Register() { CoRegisterMessageFilter(new MessageFilter(), out previous); }
        public static void Revoke() { CoRegisterMessageFilter(previous, out _); }
        public int HandleInComingCall(int type, IntPtr caller, int ticks, IntPtr info) => 0;
        public int RetryRejectedCall(IntPtr callee, int ticks, int rejectType) => rejectType == 2 && ticks < 10000 ? 100 : -1;
        public int MessagePending(IntPtr callee, int ticks, int pendingType) => 2;
    }
}
