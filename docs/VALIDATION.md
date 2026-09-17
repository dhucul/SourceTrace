# Validation — SourceTrace 0.6.1

## Release verification

- Built the complete solution using `build.ps1 -Configuration Release`, with warnings treated as errors and integration diagnostics disabled. The build completed without warnings or errors.
- All **118 automated tests passed**: 91 core/controller/navigation/export/background-I/O tests and 27 editor tests.
- The seven regression checks that failed in the logic audit now pass. Additional checks cover cancellation during initial/manual capture, cancellation during mode reads and status notifications, requests superseded by a newer command, Clear during recording, real completion after mode recovery, long-running commands after mode recovery, and pending editor-focus requests when following is disabled.
- Verified the VSIX manifest reports **0.6.1** and the packaged `SourceTrace.dll` and `SourceTrace.Core.dll` assemblies both report **0.6.1.0**.
- Compared packaged assembly SHA-256 hashes with the actual Release outputs; both match.
- Verified `SourceTrace.Test` diagnostic automation is absent from the packaged registration.
- Installer SHA-256: `1B7F5577E86957CEC4A6D66C061C3F3C4CE732C716503CFF5ACDEA697E75D9BE`.

Installer: `installer/SourceTrace.vsix`. Build log: `artifacts/release-build-0.6.1.log`. Test logs: `artifacts/test-results.txt` and `artifacts/editor-test-results.txt`.

## Validation limits

The live Visual Studio smoke test and interactive export/status display were not rerun for 0.6.1. The status display fix was checked in source and compiled in the Release package; the controller and editor fixes were exercised with production code and controlled interfaces. The existing 0.6.0 live integration evidence is preserved below as historical evidence only.

No extension was installed into the ordinary Visual Studio profile during this release build. Close Visual Studio and run the 0.6.1 VSIX to upgrade.

## Historical validation — SourceTrace 0.6.0

Environment: Windows x64, Visual Studio Professional 2026 18.10.1, .NET SDK 10.0.401. Live tests use the included C#/.NET Framework demo in the separate `SourceTrace020` profile.

## Automated checks

- Release build succeeded with warnings treated as errors.
- 73 core/controller/navigation/export/background-I/O tests passed, 0 failed.
- 26 production editor tests passed, 0 failed: 99 tests total.
- New coverage checks that earlier lines remain highlighted when the selection advances, repeated visits share one marker, trails survive file switches/reopening, Clear removes all historical markers, renamed documents cannot display markers for the old path, and reentrant notifications cannot resurrect old highlights or erase newer ones.
- The final VSIX contains the persistent trail component and excludes diagnostic automation/test hooks.
- Installer SHA-256: `CC44F9AFEE12D0745E73D2DA5415E7D55CD366F348EBD54E053EA44C64ECBD2C`.

## Visible integration check

The instrumented test build captured the actual visible WPF editor and bottom panel in a maximized Visual Studio test window. The captures were visually inspected.

- After stepping into Square and returning, earlier lines 9, 10, 11, 12, and 24 were still highlighted green, while the selected line 13 was gold.
- Continue Trace followed another 57 debugger stops and retained 19 distinct visited locations through process exit. The unvisited exception branch at line 18 stayed unmarked.
- The gold marker remained distinct from the green trail, with code text readable.
- A filter matching no trace rows preserved the historical highlights.
- Clear reduced the trail to zero, removed the current/native marker, and left no SourceTrace marker tags. The normal debugger's current-instruction indicator remains Visual Studio's own UI.
- The panel remained visible with readable rows. Closing the panel explicitly was respected across debugger layout changes, and reopening it worked.
- Trace Into, Over, Out, Pause, and Continue Trace passed. The visible run observed 67 debugger stops in total.

Log: `artifacts/final-visible.log`. Captures: `artifacts/final-visible/{statement,finished,cleared}/editor.png`.

## Packaged release check

The final installer, built without diagnostic hooks, is installed only in the experimental profile for the standard smoke test. The release test covers package loading, all three manual steps, source navigation, Pause, and continuous tracing through exit. Log: `artifacts/release-smoke.log`.

The ordinary Visual Studio profile is not upgraded automatically. Close Visual Studio and install the supplied 0.6.0 VSIX to update it.

## Scope

The trail represents recorded debugger source stops. It does not infer unobserved statements inside Trace Over/Out calls, other threads, or filtered-out framework code. The complete retained trace drives the trail independently of the table's display filter and Follow latest option. Clear resets it; a new debug run otherwise continues the retained history.

Other themes/display scales, native/mixed-mode targets, async/multithreaded programs, and interactive export dialogs remain additional manual acceptance scenarios. See README.md and TESTING.md.
