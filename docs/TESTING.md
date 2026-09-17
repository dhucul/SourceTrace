# Testing SourceTrace

## Automated controller and export tests

Run `build.ps1` at the repository root. It builds with warnings treated as errors and runs **118 tests**: 91 controller/navigation cases and 27 editor cases. Coverage includes manual/automatic stepping, cancellation during capture and notifications, context notifications, fresh source validation, mode-read failures with an outstanding command, missing completion notifications, live settings, omission metadata, source-result identity, deep export snapshots, atomic file replacement, background source I/O, filtered source selection, stale-history rejection, explicit focus, freezing pending navigation, and reentrant request invalidation. Editor tests cover obsolete gold-marker construction, accumulated trails, loop deduplication, file switching/reopening, rename, Clear, disposal, reentrant Clear/addition, and independent high-contrast styling.

The core tests use a fake debugger and deterministic scheduler. They validate scheduling and state transitions without requiring a running Visual Studio instance.

`tests/SourceTrace.EditorTests` compiles the production editor component source against the same .NET Framework target as the VSIX, using controlled editor-interface proxies. It exercises actual tagger code for rename/path changes, Clear, view disposal, projection subject buffers, and out-of-range lines, plus live high-contrast transitions and shared-format-map cleanup. The test-only MessagePack dependency is pinned independently; it is not included in the installer.

## Actual Visual Studio smoke test

`tests/SourceTrace.Smoke` drives a dedicated Visual Studio instance using Microsoft's DTE automation API. It connects only to the supplied process ID, waits for that instance to open the supplied demo solution, builds the demo, exercises the extension's registered commands, and closes that test instance afterward. **Only supply the PID of a test instance created for this purpose**, since its solution will be closed when the test finishes. Launch Visual Studio with the solution first: `devenv.exe "<demo-solution-path>" /RootSuffix <test-suffix> /Log`. Keep `/Log` last or supply an explicit log filename; its optional filename must never be the solution file.

The smoke test verifies:

1. The package and menu command load and the SourceTrace tool window exists.
2. Trace Into launches debugging and reaches the first source statement.
3. Trace Into enters `Square`.
4. Trace Out returns to `Main`.
5. Trace Over stays in `Main`.
6. Pause prevents further automatic steps.
7. Continue Trace resumes and follows further source stops, including recursive `Factorial`, until the process exits.

After manual steps, the test also checks that the editor has the correct source document and caret line. The VSIX declares its editor marker as a MEF component. The integration log and the test profile's composition error log should contain no SourceTrace load/composition errors.

The test does not evaluate user variables or call methods in the target program through the debugger. It only executes the included demo normally under debugger step commands.

To run it again, create an experimental Visual Studio profile using the installed VSSDK's `CreateExpInstance.exe`, deploy the extension to that profile, and start `devenv.exe /RootSuffix <your-test-suffix> /Log`. The profile name and installation ID vary by machine. Microsoft's VSSDK MSBuild deployment properties are `DeployTargetInstanceId`, `VSSDKTargetPlatformRegRootSuffix`, and `DeployExtension=true`, with the `Build;DeployVsixExtensionFiles` targets. Do not point them at your ordinary profile.

Then run:

```powershell
.\tests\SourceTrace.Smoke\bin\Release\net472\SourceTrace.Smoke.exe <test-devenv-PID> .\samples\TraceDemo\TraceDemo.slnx
```

Allow the experimental profile to complete first-run setup before testing. If it has not registered an automation object yet, the connection test times out after two minutes. Debugger events in the external test process only record notifications; stack-frame reads occur after the callback returns to avoid reentrant debugger calls.

The helper has a four-minute process-wide deadline, including synchronous COM calls outside its polling loops. A caller should retain the test Visual Studio process handle and close that dedicated instance if the helper exits on the deadline. After a version upgrade, use the actual VSIX installer with `/quiet /rootSuffix:<your-test-suffix> /instanceIds:<test-installation-id>` if the SDK deployment leaves stale registration. Run `devenv /RootSuffix <your-test-suffix> /UpdateConfiguration` after the experimental profile has completed first-run setup and after installation to ensure the package registration is imported. Launch with the demo solution path to avoid waiting on the start window. Do not omit the root suffix when testing against a separate profile.

## Manual acceptance checks

### Visible integration diagnostics

For a dedicated test build only, pass `/p:EnableIntegrationDiagnostics=true` to MSBuild when compiling the VSIX. Install that VSIX into the experimental profile and pass an output directory plus `--visible` to the smoke helper:

```powershell
.\tests\SourceTrace.Smoke\bin\Release\net472\SourceTrace.Smoke.exe <test-devenv-PID> .\samples\TraceDemo\TraceDemo.slnx .\artifacts\visible --visible
```

This opens/maximizes that test window and captures the actual WPF editor and panel at entry, after returning from Square, after process exit, and after closing/reopening the panel. It checks that both visuals and the selected row are visible, a native marker exists, and horizontal scrolling stays at zero. It also checks that native stepping does not reopen an explicitly closed panel. Inspect the PNGs to confirm the actual highlight and readable trace rows; caret position and tagger data alone do not prove visible highlighting.

Version 0.6 also checks the rendered trail tags for earlier lines 10 and 24 after stepping onward, ensures the unvisited exception branch (line 18) remains unmarked, verifies a no-match filter preserves the trail, and captures the editor after Clear to confirm both highlight types disappear.

Build the final installer with the normal `build.ps1` and verify `SourceTrace.Test` is absent from its package registration. Never distribute the instrumented diagnostic installer.

### Additional scenarios

- Install the VSIX in Visual Studio 2026, restart, and open Tools > SourceTrace.
- Check the trace window in light and dark themes and at your preferred display scaling.
- Check the panel stays visible while entering/exiting debugging, and trace rows remain accessible in a shallow dock. Expand the status line to inspect the selected statement.
- Hover and press each enabled toolbar button; confirm the text stays readable. Test keyboard focus and disabled buttons as well.
- Confirm the gold source marker remains visible when focus returns to the trace window, follows trace steps, and disappears on Clear.
- Confirm earlier visited lines remain green as the current instruction advances, including across functions/files and after program exit. Clear must remove the entire trail.
- Select an earlier trace row, verify its file/line is highlighted, then re-enable Follow latest.
- Use the demo to verify the three manual steps and continuous tracing.
- Place a breakpoint later in the demo: Continue Trace should pause there. Disable Pause at breakpoints in Settings to permit automatic tracing through it.
- Run the demo with `--exception`: automatic tracing should pause at the unhandled exception according to Visual Studio's exception settings.
- Pause during a longer automatic trace and confirm it stays paused.
- Set a low record limit and use a long loop; recording should stop at the limit without discarding earlier rows.
- Export JSON and CSV, confirm all rows are included, and double-click a recorded location to navigate to its file and line.
- Try a C++ project with source and symbols if you need native tracing. The DTE adapter supports debugger source frames, but the initial end-to-end verification used the included C#/.NET Framework demo.

Visual rendering across additional themes/display scales, native/mixed-mode debugging, async/multithreaded target programs, and the interactive save dialog remain manual acceptance scenarios. Editor state transitions are tested with production code and controlled interfaces; the live smoke test confirms actual IDE loading, stepping, source navigation, and Pause. Automated tests do not establish exhaustive all-thread tracing coverage.
