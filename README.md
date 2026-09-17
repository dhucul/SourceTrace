# SourceTrace 0.6.1

A source tracing extension for **Visual Studio 2026 on Windows x64**. Trace into, over, or out of functions, or use Continue Trace to repeatedly step through source statements until the program exits or tracing pauses.

## Install and use

1. Close Visual Studio and double-click `installer\SourceTrace.vsix` (or the supplied `SourceTrace.vsix`). Select your Visual Studio 2026 installation. This locally built installer is unsigned.
2. Start Visual Studio and open your project in **Debug** configuration with its symbols available.
3. Open **Tools > SourceTrace**. You can also use **Debug > SourceTrace**.
4. Choose **Trace Into** to launch at the first debuggable statement. Alternatively, start debugging normally, stop at a breakpoint, and open SourceTrace.
5. Choose a tracing control:

| Control | Behavior |
| --- | --- |
| Trace Into | Execute one source step, entering a called function when the debugger can do so. Can launch debugging. |
| Trace Over | Execute one step while remaining in the current function. Called functions still run. |
| Trace Out | Execute until control returns to the caller. |
| Continue Trace | Repeatedly Trace Into, recording each debugger source stop. Can launch debugging. |
| Pause | Cancel automatic stepping and request a debugger break if the program is running. |
| Clear | Clear the retained trace while paused. |
| Export | Save all retained records to JSON or CSV, including records outside the visible list or filter. |

**Visited source lines stay highlighted in green as you step onward.** The current/selected trace row is highlighted in gold. The green trail accumulates from recorded debugger stops, including when Follow latest is off, and remains after program exit until you press **Clear** or close Visual Studio. Repeated visits to the same line reuse its highlight.

With **Follow latest** enabled, the table and editor follow the same newest row matching the current filter (within the newest 5,000 records). Its source line is revealed in the editor and marked with a translucent gold highlight and border. Highlights remain visible while the trace window has focus and preserve the code's syntax colors. No matching rows means no selected row or gold marker; the green trail remains.

Select a history row to show and highlight that row's source line. This turns off Follow latest so new trace rows do not replace your selection. Turning off Follow latest also lets any pending navigation for the selected row finish. Double-clicking or pressing Enter also turns following off and transfers keyboard focus into the editor, even when the row was already selected. Re-enable Follow latest to return to the newest matching row. The filter searches function names, source text, filenames, and stop reasons, and automatically accounts for source text that finishes loading later.

Clear immediately removes the visible rows, selection, and all SourceTrace highlights. Input from an old row cannot restore a cleared highlight. Filtering, selecting older rows, and switching/reopening source files preserve the green trail. It covers all retained trace locations, including rows outside the newest-5,000-row display window. If the selected row stops matching the filter or leaves that window, its selection and gold marker are cleared. Failed navigation clears the gold marker. Rename/Save As prevents highlights for the old path from being shown in the renamed document.

SourceTrace's buttons use Visual Studio's theme-aware button style, including hover, pressed, focused, and disabled colors.

SourceTrace stays pinned while you trace, including when Visual Studio switches between its editing and debugging layouts. Closing it explicitly keeps it closed until you open SourceTrace or use a SourceTrace command again. The compact toolbar and collapsed statement details leave room for history rows in a shallow bottom panel. Expand the status line below the table to see the selected statement and activity details. You can drag the panel's top edge to show more rows.

SourceTrace also creates a native editor line marker alongside the gold marker, so the selected source location remains marked when the editor is inactive. Following a long statement reveals its beginning without scrolling sideways to its trailing comment. Visited-line and selected-line colors can be customized under Visual Studio's Fonts and Colors settings as **SourceTrace visited line** and **SourceTrace traced line**.

The marker responds to high-contrast changes while the editor is open and restores its normal/custom colors afterward. File-backed subject buffers in projection editors are supported; the editor maps their marker spans into the view.

## Continuous tracing

Continue Trace follows the debugger's selected execution flow. It stops at breakpoints by default, and always pauses on reported exceptions, user breaks, missing source, unexpected stops, or the record limit. Use Continue Trace again to resume. If the program waits for input, supply the input in the program or choose Pause.

**Tools > Options > SourceTrace > Tracing** controls:

- Delay between automatic steps: **30 ms** by default; valid range 1–5000 ms.
- Maximum retained records: **100,000** by default; valid range 100–1,000,000. Export and Clear to collect more when the limit is reached.
- Pause at breakpoints: enabled by default. Turn this off when you want continuous tracing to pass through breakpoints. Visual Studio's exception settings still determine which exceptions produce debugger stops.

The visible list shows the newest 5,000 records. Export includes every retained record. Closing the trace window does not cancel tracing; use Pause first if you want to stop.

Saved settings are loaded when the extension starts. Applying changes updates the active trace: a queued step adopts the new delay, breakpoint policy applies to subsequent stops, and reducing the limit below the retained count pauses automatic tracing without deleting records. Selecting a different debugger thread/process/frame cancels a queued automatic step; choose Continue Trace to resume in the selected context.

At capacity, recording displays a persistent warning. Native F10/F11 may still execute, but every omitted stop is counted. Increasing the limit permits further recording while retaining the omission warning. Clear resets the history and its omission count, and is rejected while a debugger command is still in flight.

## What is recorded

Each row records the debugger's current source location **before the next statement executes**: sequence, UTC time, elapsed wall time, process, thread, frame position, function, module, file, line, source text, operation, and stop reason. **Frame** is the selected frame's one-based position in the current stack; the top frame is 1. It does not measure call nesting. Repeated visits to the same line are retained. The location comes from the current debugger stack frame, independent of the open editor or caret.

Source text is read from disk by a bounded background worker, limited to 2,048 characters per line and files up to 2 MiB. Slow or disconnected source paths do not hold the debugger UI thread. Locations remain recorded if text is unavailable or the 256-request queue is full. Late results are attached only to the original retained row; Clear and shutdown invalidate them. Unsaved edits or source differing from the binary can make the displayed snippet differ from executing code. No variable/property evaluation is performed.

### Export format and recovery

JSON format version **2** uses `frameIndex` instead of the old misleading `depth` field. It includes `recordLimit`, `atCapacity`, `droppedStops`, and `recordingInterrupted` metadata. CSV includes corresponding metadata columns. Each row also has `sourceState`: `Pending`, `Available`, or `Unavailable`. Export takes a deep snapshot when the save dialog is accepted; source reads that finish later do not change that exported snapshot.

Exports write to a temporary file in the destination directory and replace the destination only after writing and flushing succeed. A failed or canceled write preserves the previous file. Export/navigation feedback appears in the expanded activity details and cannot hide tracing status or cancel a newer trace.

If a debugger mode read fails, automatic tracing stops and the history is marked potentially incomplete. An outstanding command stays protected against another step or Clear until its completion or recovery. A background state poll permits recovery when Visual Studio is available again. If an outstanding step remains in a nonrunning debugger mode for about 15 seconds without its completion notification, SourceTrace releases its pending state and stops automatic tracing; it does not automatically retry the command. Long-running steps are not timed out while the debugger reports Run mode. A normal paused-mode recovery is recorded explicitly.

## Scope and limits

- Continuous tracing uses repeated debugger steps and is substantially slower than normal execution. It changes program timing and is not a performance profiler.
- This is a history of debugger source stops, not exhaustive instrumentation of every statement in every process and thread. Other threads may run between steps; their intervening statements are not captured. Async code, optimized code, debugger step filters, Just My Code, and missing symbols affect the path that Visual Studio exposes.
- To follow the program from its beginning, open SourceTrace first and launch using Trace Into or Continue Trace. Starting from a later breakpoint records only subsequent execution.
- Trace Over and Trace Out do not record the internal statements they run through. Ordinary Visual Studio Continue/F5 resumes normal execution; use SourceTrace's Continue Trace for repeated stepping.
- Native F10/F11 stops are recorded after SourceTrace has been opened, but the extension never remaps those shortcuts. You may assign SourceTrace commands under Tools > Options > Environment > Keyboard.
- Missing-source stops are retained, and automatic tracing pauses. Load symbols/source, enable Just My Code where appropriate, or use Trace Out to return to your code.
- History lives in memory until Clear or Visual Studio exits. Export to keep it. Elapsed time includes pauses and user interaction; it is not CPU time.

## Try the demo

Open `samples\TraceDemo\TraceDemo.slnx`. It contains a loop, a `Square` call, and recursive `Factorial` calls. Use Trace Into at the call to enter Square, Trace Out to return, then Continue Trace to finish. The expected program output is:

```text
Total = 30
Factorial(4) = 24
Trace complete.
```

The optional `--exception` program argument demonstrates an unhandled exception stop. The demo has debugging symbols and optimization disabled in both build configurations.

## Build

Requirements: Visual Studio 2026 18.5 or later, the **Visual Studio extension development** workload, .NET Framework 4.7.2 targeting pack, and the .NET 10 SDK for tests. NuGet restores Microsoft's SDK packages on the first build.

```powershell
.\build.ps1
```

This builds the solution, runs the automated controller/export tests, and creates `installer\SourceTrace.vsix`. It does not install the extension into your normal Visual Studio profile.

For development, open `SourceTrace.slnx`, choose `SourceTrace.Vsix` as the startup project and press F5 to launch the Visual Studio experimental instance. The solution marks the extension project for deployment in Debug configuration.

## Structure

- `src/SourceTrace.Core`: debugger-independent trace controller, records, and export writers.
- `src/SourceTrace.Vsix`: Visual Studio debugger adapter, package/menu registration, options, and WPF trace window.
- `tests/SourceTrace.Tests`: executable behavioral tests with a deterministic fake debugger and scheduler.
- `tests/SourceTrace.Smoke`: actual Visual Studio automation test, for a dedicated test instance only. See `docs/TESTING.md`.
- `samples/TraceDemo`: small program for testing tracing manually.

The extension uses Microsoft's VSSDK and the 17.14 API baseline. Visual Studio 2026 uses the 17.x API compatibility model; the manifest's `[17.14,)` target is intentional. Visual Studio 2015/2017 and 32-bit installations are not targeted.

## API references

- [Visual Studio extension compatibility](https://devblogs.microsoft.com/visualstudio/modernizing-visual-studio-extension-compatibility-effortless-migration-for-extension-developers-and-users/)
- [SDK-style extension projects](https://devblogs.microsoft.com/visualstudio/sdk-style-support-for-extension-projects/)
- [Debugger source stack frames](https://learn.microsoft.com/en-us/dotnet/api/envdte90a.stackframe2?view=visualstudiosdk-2022)
- [Debugger stop reasons](https://learn.microsoft.com/en-us/dotnet/api/envdte.dbgeventreason?view=visualstudiosdk-2022)
