# ADR 0002: What macOS parity means — port the gaps, keep self-update on Windows

**Status:** Accepted
**Date:** 2026-10-08
**Context:** [issue #11](https://github.com/JollyKrampus/loglens/issues/11) — macOS parity

## Context

ADR 0001 shipped the Avalonia shell with four known gaps against the WPF app:
find-in-tab, editor/Explorer integration, alerts and sounds, and self-update. It
called them "ports-not-designs": the logic was already in `LogLens.Core` or was
Windows-specific by nature. Issue #11 asked which of them to close, and how.

The constraints that mattered:

- **Two shells, one core.** The reason `LogLens.Core` exists is that the two apps
  bind the same view-models and so cannot drift. Any logic a port needs that still
  lived in WPF code-behind was a place they would.
- **The workspace is shared across platforms and versions.** A Windows user and a
  Mac user can open the same `loglens.workspace.json`; it is a compatibility
  contract, and settings must be additive with defaults.
- **No Apple Developer account.** The `.app` is unsigned and un-notarised; users
  clear Gatekeeper with `xattr -cr`. Anything that needs a signed bundle identity
  (native notification APIs, notarised auto-update) is out of reach.
- **Portable-first and dependency-light.** No new NuGet packages, no helper
  binaries to ship, no installer.

## Decision

Port find-in-tab, editor/Finder integration and alerts to the Avalonia app.
Keep self-update Windows-only. Keep signing and notarisation deferred.

## How each gap was closed

1. **Find-in-tab.** The matching and stepping logic — plain or regex, match case,
   a hit list cached against the displayed line count, next/previous from the
   selection with wrap at both ends — moved out of the WPF `LogPane` code-behind
   into `LogLens.Core/Services/LineFinder.cs`, with a RuleCheck section. Both
   shells now wrap the same object in their own find bar. The Avalonia shortcuts
   take the command modifier from Avalonia's `PlatformHotkeyConfiguration`, so the
   Mac gets Cmd+F / Cmd+G / Shift+Cmd+G and Windows/Linux get Ctrl; F3 and
   Shift+F3 work everywhere.

2. **Open in editor / reveal.** `LogLens.Avalonia/ShellOpen.cs` mirrors the WPF
   `ShellOpen` contract (return an error string, never throw; run off the UI
   thread) using what every Mac already has: `open -t` for the default text editor
   and `open -R` to select the file in Finder. `-t` rather than a bare `open`
   because macOS associates `.log` with Console.app, which is a viewer, not an
   editor. Linux falls back to `xdg-open` on the file and on its folder. The entry
   points match WPF: Files menu, an Editor toolbar button, and the log context menu,
   which on the merged view acts on the selected line's source file.

3. **Alerts.** The decision — thresholds, per-view mute, the foreground check, the
   custom pattern, per-view throttling — was never Windows-specific; it just lived
   in the WPF `AlertService`. It moved to `LogLens.Core/Services/AlertPolicy.cs`
   along with the notification wording, and the existing RuleCheck alert checks
   now run against it unchanged in substance. Delivery stays per shell: WPF keeps
   the tray balloon, taskbar flash and `.wav`; the Avalonia `DesktopAlerts` posts a
   Notification Center banner through `osascript` and plays a system sound through
   `afplay` (Linux: `notify-send`, no sound). The log text in a notification is an
   injection surface, so it is passed to `osascript` as argv against a constant
   script, never spliced into AppleScript source. Sound names are checked against
   the `/System/Library/Sounds` list, never used as a path.

   The workspace already stored Windows sound ids (`"Windows Notify.wav"`).
   Rather than reinterpret those fields per platform — which would let a Mac
   teammate's choice overwrite a Windows one in a shared workspace —
   `AlertSettings` gained **new, additive** fields `MacSoundName` (default
   `Glass`) and `MacFatalSoundName` (default `Basso`). Old workspaces load with the
   defaults, older versions ignore the fields, and `Workspace.CurrentVersion` is
   unchanged because the default rules did not change.

## Why self-update stays Windows-only

The Windows updater is built on a Windows quirk: a running `.exe` cannot be
overwritten but can be renamed, so the app renames itself to `.old`, drops the
verified download in its place, starts it and exits. None of that carries over:

- A macOS install is a `.app` **directory**, not one file. Replacing it in place
  means swapping a bundle the running process is executing from, and wherever the
  user dragged it (often `/Applications`) may not be writable without elevation.
- A downloaded bundle arrives **quarantined**. Gatekeeper blocks an unsigned,
  un-notarised app on first launch; the user's `xattr -cr` is what clears it today.
  An updater that silently cleared quarantine on its own download would be
  bypassing the one check macOS gives the user — not something to do on their
  behalf.
- The right macOS answer is a signed, notarised bundle with a standard updater
  (Sparkle or similar), which needs the Apple Developer account we don't have.

So the Mac app keeps checking nothing and updating nothing: users download the new
tarball from the releases page as they do now. Both shells still call
`UpdateService.WaitForPredecessor` and `CleanUpLeftovers` at startup, so the
handoff protocol stays shared should that change.

## Why notarisation stays deferred

Signing and notarisation need an Apple Developer Program membership ($99/year).
The `build-macos` job already has a disabled **Codesign and notarise** step
(`if: ${{ false }}`) waiting for `APPLE_CERT_P12` / `APPLE_ID` secrets. Until then
releases stay unsigned and the README's `xattr -cr` instruction stands. Nothing in
this decision depends on signing; when it arrives, it is the natural moment to
revisit native notifications (UserNotifications instead of `osascript`) and a Mac
updater.

## Trade-offs accepted

- **A banner posted via `osascript` belongs to Script Editor.** Clicking it opens
  Script Editor rather than jumping to the view that raised it, as the Windows
  balloon does. Fixing that needs a native notification API and a signed bundle
  identity.
- **No taskbar-flash equivalent on macOS.** Bouncing the Dock icon needs native
  `NSApplication.requestUserAttention`, which Avalonia does not expose; the Mac
  Alerts dialog leaves the option out rather than offer a checkbox that does
  nothing.
- **The Mac sound list is the fourteen system sounds.** No "Browse for your own
  file" as on Windows — a path stored in a shared workspace would point at a file
  only one teammate has.
- **Not exercised on a real Mac at the time of writing.** The Linux paths
  (`xdg-open`, `notify-send`) and the shared logic were tested; the `open`,
  `osascript` and `afplay` invocations follow their documented interfaces.

## Consequences

- The remaining shell differences are deliberate: self-update UI (Windows-only by
  design), the taskbar flash, and the click-to-focus on a notification.
- Logic either shell needs lives in Core with a RuleCheck case — `AlertPolicy`
  and `LineFinder` joined the rest. `tests/RuleCheck` still targets Windows only
  for `SoundLibrary`.
- The Avalonia shell grew from eight source files to ten (`ShellOpen.cs`,
  `DesktopAlerts.cs`); it is still thin.
