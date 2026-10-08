# Changelog

What changed in each release, written for the people running it. The GitHub
release pages carry the auto-generated PR list; this file says what those PRs
meant. Versions follow the tags (`v1.5.3` → `1.5.3`).

## Unreleased

### Added
- **Find, editor/Finder and alerts on macOS.** The Mac app gains find-in-tab
  (`Cmd+F`; `Cmd+G` / `Shift+Cmd+G` or Enter / Shift+Enter to step), **Editor** and
  **Show in Finder** on the Files menu, the pane toolbar and the log's right-click
  menu (on the merged view they act on the selected line's file), and alerts: the
  same thresholds, per-view mute, focus check, custom pattern and throttling as
  Windows, delivered as a Notification Center banner and a macOS system sound
  (*Glass* / *Basso* by default), with an **Alerts** menu and settings dialog. The
  Mac sound choice is stored separately, so a workspace shared with Windows
  teammates keeps theirs. Self-update stays Windows-only — see
  `docs/adr/0002-macos-parity-scope.md`. ([#11](https://github.com/JollyKrampus/loglens/issues/11))

### Changed
- **Issue grouping masks short ids and numbered names.** Short hex ids
  (`correlationId=5747bc31`) and digits inside lowercase names (`node7`,
  `web03`, `worker_12`) no longer split one fault into an issue per host or
  request. Issues already stored for messages containing such tokens stop
  growing; new sightings start a fresh, correctly grouped issue beside them —
  their history, counts and Jira keys are kept, not merged.

## 1.5.4 — 2026-10-08

### Added
- **Issues by application.** The Issues window has an **Application** dropdown —
  every log file in the selected view, by its tab name, with how many distinct
  issues it holds, busiest first — and a sortable Application column. Picking one
  scopes the list and the Fatal / Error / Warn counts. Column sorts now survive a
  refresh. Both the Windows and macOS apps. ([#16](https://github.com/JollyKrampus/loglens/issues/16))
- **Scoop and winget manifests.** `bucket/loglens.json` makes this repository a
  Scoop bucket; `packaging/winget/` holds manifests ready to submit to
  `microsoft/winget-pkgs`.

### Changed
- A `loglens.not-portable` file beside the exe sends the workspace and issue
  database to the per-user folder instead of beside the exe. The Scoop manifest
  creates it, because Scoop installs each version into a new folder and a
  workspace kept beside the old exe was lost on every `scoop update`.

### Fixed
- **Self-update refuses a download it cannot verify.** A release without a
  `SHA256SUMS.txt`, a sums file with no `LogLens.exe` line, or a proxy answering
  the sums URL with an HTML page all used to skip verification silently and
  install the download anyway. Each is now refused with a readable message.

## 1.5.3 — 2026-08-18

### Fixed
- **Multi-line events no longer mint false Fatals.** In a file whose lines carry
  timestamps, a line without one is a continuation of the event above it, and loose
  severity keywords inside it no longer count — so captured
  `STDOUT: ****Fatal error received…` spill under an `|ERROR|` event stays part of
  that error.
- Timestamp detection uses a settled verdict, never a provisional guess. One
  warm-up line that merely mentioned a date could otherwise strip every colour,
  chip, alert and issue from a keyword-only file.
- Stack frames after free-form spill lines stay attached to their issue.
- A slow startup update check can no longer stack a second update dialog over a
  manual one.

### Changed
- Update-check results are impossible to miss: "you're on the latest version" and
  a failed manual check are message boxes, and an update found at startup opens
  the update dialog directly. Startup check failures stay quiet.
- **macOS ships `LogLens.app`** with its own icon, Dock identity and double-click
  launch. A bundle is not a portable install, so on macOS the workspace lives in
  `~/.config/LogLens/`.

## 1.5.2 — 2026-08-18

### Fixed
- Stored issues follow re-classification: a signature the old rules called Fatal
  no longer stays Fatal in the Issues window after the rules are fixed.
- Exception headers, stack frames and inner-exception markers sit above the keyword
  tier, so a stack line saying "fatal" reads as detail of its parent event.
- The inner-exception rule is anchored to the start of the line; a bare `--->`
  match had dropped real `ERROR … ---> IOException` event lines to no severity.
- The 1.5.1 rule fix now reaches workspaces saved by 1.5.1 (workspace version 3).

### Changed
- When a tab is clearly pipe-delimited but the rules still use loose keywords, the
  status bar suggests the anchored NLog preset once.
- The macOS build is one self-contained `LogLens` binary — native libraries
  embedded, not shipped beside it — with Gatekeeper steps in a bundled README.

## 1.5.1 — 2026-08-18

### Fixed
- **The level field outranks message keywords.** Default rules are two tiers: an
  anchored `|LEVEL|` field tier first, keyword matching as the fallback. An
  `|Error|` line whose message says "Fatal error received" is an Error. Untouched
  old default rule lists upgrade on load once; edited lists are never touched.
- The self-update saves the workspace and closes the issue database *before*
  starting the new version, so the outgoing instance no longer dies mid-save.
- A failed swap restores auto-save and issue recording instead of silently
  dropping every later change; a locked `.old` file no longer fails the update.
- The released exe reported 1.5.0 and offered itself as an update forever.

## 1.5.0 — 2026-08-18

### Added
- **macOS app** (Avalonia) over the same core as the Windows app: views, tabs,
  highlight colours, severity chips and filters, the merged timeline, rule
  editing, and the per-view Issues window with Jira copy. Self-contained
  `osx-arm64` and `osx-x64` downloads on each release, with checksums.
- **Pane state persists**: severity chips, Show/Hide filters and follow, per file
  tab and per merged view.
- **Auto-save**, on by default: a few seconds after anything changes, the
  workspace is on disk. Turn it off in Settings.

### Fixed
- The new version waits for the old one to exit before touching the workspace or
  issue database; a briefly locked database is retried instead of crashing.

## 1.4.0 — 2026-08-18

### Added
- **Issues are kept per view.** The same fault in Dev and Prod is two issues with
  independent counts, Jira keys and ignore flags. Databases from 1.3 migrate
  automatically, keeping every count, key, note and ignore flag.
- The merged view's context menu works: open the selected line's file in an
  editor or Explorer, or **go to this line's file tab** with the exact line
  selected.

### Changed
- Everything that doesn't touch WPF moved into a cross-platform core library — the
  groundwork for the macOS app.

### Fixed
- The 1.3 → 1.4 database migration no longer loses the count index.
- Go-to-source lands on the right occurrence of a repeated line, and says so when
  filters or trimming hide the target.

## 1.3.0 — 2026-08-18

### Added
- **Self-update**: a quiet startup check (off switch in Settings) and **Help ▸
  Check for updates**, which downloads, verifies against the release's SHA-256
  checksum, swaps the exe in place and restarts — no installer, no admin rights.
- Startup progress ("Opening logs… n/m") while the initial reads run.
- The workspace format is a tested contract: CI fails if a v1.1.0 workspace stops
  loading losslessly, and unknown fields from newer versions are ignored.

## 1.2.0 — 2026-08-18

### Added
- **Severity chips** — F / E / W / I / D toggles that filter by any combination at
  once, with stack traces staying attached to the line they belong to.
- **Open in editor** and **Show in File Explorer** from the toolbar, right-click
  and the Files menu; a wildcard tab opens the file it's tailing right now.
- A splash screen covering the single-file startup pause.

### Fixed
- `build.ps1 -Mode Framework` always failed (NETSDK1176).
- Chip colours, toolbar clipping, "Clear filters" leaving chips on, and a blank
  Notepad opening for a file deleted mid-click.

## 1.1.0 — 2026-08-16

The first tagged release.

- Real-time tailing with highlight rules, saved views and per-view error badges.
- **Merged timeline** across every file in a view.
- **Alerts** with a sound picker and a distinct sound for FATAL.
- **Issue database**: repeated faults grouped into distinct problems by a
  deterministic signature, with ready-to-paste Jira tickets.
- A SHA-256 checksum published with each release.
