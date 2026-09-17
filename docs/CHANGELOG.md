# 0.6.1

- Revalidate command ownership after debugger reads and status notifications. Pause, Clear, disposal, and newer requests cannot allow an older pending request to issue a debugger step.
- Preserve outstanding-command protection across temporary debugger-state failures. Completion events and timeout recovery still settle the original command; transient failures cannot enable overlapping steps or premature Clear.
- Keep tracing status visible after export or navigation feedback; display file-operation messages separately in activity details.
- Turning off Follow latest freezes the selected record while allowing its pending navigation and focus request to finish.
- Reject obsolete gold-marker updates after a newer selection arrives during tracking-span construction.
- Expand automated coverage to 118 tests: 91 core/controller/navigation/export tests and 27 editor tests.

# 0.6.0

- Retain green highlights on every recorded source line as tracing moves onward; keep the current/selected line gold. The trail persists across files, editor reopening, filters, manual history selection, and program exit until Clear or IDE shutdown.
- Record the trail from debugger stop events, independently of deferred editor navigation. Fast continuous tracing and disabled Follow latest cannot skip trail updates. Repeated visits to a line share one highlight.
- Clear removes both the trail and current marker. Guard against old notifications and reentrant span creation republishing cleared highlights; preserve newer visits arriving during Clear notifications.
- Add a separate theme-aware visited-line format and retain syntax colors. Both marker formats respond to high-contrast changes.
- Expand regression tests to 99 (73 core, 26 editor). Extend visible integration checks to assert earlier lines remain highlighted, filtering preserves the trail, unvisited branches stay unmarked, and Clear removes all extension highlights.

# 0.5.0

- Add a native editor line marker alongside the MEF gold highlight, including inactive editor views. Clean up native markers on Clear, document rename, cancellation, and shutdown; stale cleanup cannot remove a newer marker.
- Keep SourceTrace pinned and restore its visibility after debugger layout changes and source navigation. Subscribe to actual window-close notifications so an explicit close remains respected.
- Reveal the beginning of a source line instead of its entire width, avoiding horizontal scrolling to trailing comments.
- Compact the toolbar, filter, and status layout; collapse statement details so shallow bottom panes still show trace rows. Register a larger default docked height without attempting unsupported runtime resizing.
- Expand editor tests to 16 cases (89 total automated tests). Add opt-in live diagnostics that capture actual visible editor/panel rendering and check row visibility, horizontal scrolling, and close/reopen behavior. The diagnostics automation object is excluded from normal release builds.

# 0.4.0

- Use one authoritative selected record for the grid, details, and editor. Follow latest now follows the newest matching row within the visible history window.
- Validate record identity before accepting navigation and before committing editor changes. Clear immediately resets the visible rows and rejects stale input from cleared histories.
- Carry request ownership through document opening, caret/scroll changes, marker publication, and focus restoration. Superseded failures cannot remove a newer marker or overwrite newer feedback.
- Clear old markers before new navigation, and keep them cleared when opening/validating the new target fails.
- Every explicit selection/open pauses automatic following, preserving double-click/Enter focus requests.
- Invalidate markers on document rename/reload and defensively check document ownership when returning tags.
- Support file-backed subject buffers in projection views and prevent orphan subscriptions for unsupported tag types.
- Update high-contrast marker styling live, share one override per editor format map, and restore user styling when high contrast ends or the last view closes.
- Expand tests to 73 controller/navigation cases and 12 editor cases; retain live Visual Studio stepping/navigation validation.

# 0.3.0

- Highlight the traced source line directly in the editor using a persistent, translucent gold marker.
- Follow new source locations automatically; coalesce navigation outside debugger callbacks.
- Selecting a history row shows/highlights its source and turns off automatic following. Re-enabling Follow latest returns to the newest location.
- Clear removes the source marker; closing views detaches their marker subscriptions.
- Use Visual Studio's button style so text remains readable when hovered, pressed, focused, or disabled.
- Add navigation-state regression coverage and live editor-file/line checks.

# 0.2.0

Addresses the nine audit findings and additional lifecycle/UI gaps:

- Catch debugger-state errors at deferred boundaries; expose cached mode to UI code and recover without an automatic retry.
- Subscribe to real debugger context changes, compare stable contexts, and revalidate again before queued steps.
- Read a fresh location for command eligibility independently of history deduplication.
- Load saved options at startup and apply validated changes to active tracing. Preserve the original options-page GUID across upgrades.
- Separate debugger errors from export/navigation feedback; ignore obsolete asynchronous operation messages.
- Replace exported files transactionally, preserving the old file after write failure or cancellation and avoiding overwriting a concurrently created destination.
- Retain capacity/omission warnings and include recording completeness metadata in exports.
- Execute Pause before any optional window operations.
- Rename the misleading Depth field to Frame / frameIndex; JSON format is now version 2.
- Reject Clear during outstanding steps and clear all selected-row UI fields together.
- Recover missing step completion notifications in stable nonrunning modes; prevent accidental relaunch after a missing session-end event.
- Resolve source text on a bounded background worker; discard stale results and deep-copy export snapshots.
- Guard/retry UI refresh failures and bound the entire integration test, including blocked COM calls.

Normal Visual Studio installations are upgraded by running the rebuilt VSIX after closing the IDE. Tests use a separate experimental profile.
