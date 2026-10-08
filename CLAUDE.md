# CLAUDE.md

Guidance for AI assistants working in this repository. Read this before changing code.

The comments in this codebase are unusually load-bearing: most of them record a bug
that was already shipped once. When a comment says "do NOT do X", it means someone
did X and it broke. Prefer reading the comment over re-deriving the rule.

---

## What LogLens is

A portable real-time log viewer — a BareTail replacement with saved *views* (named
groups of log files: Dev / Test / Prod), live filtering, per-view error badges, a
merged cross-file timeline, and a local SQLite database that groups repeated errors
into distinct issues you can turn into a Jira ticket.

No installer, no service, no account, no telemetry, no network calls except one
optional GitHub-releases check. Windows (WPF) is the flagship; macOS (Avalonia) is
an early port. `README.md` is the user-facing manual and explains *why* most
features are shaped the way they are — it is worth reading in full before design work.

Current version: **1.5.4** (see "Releasing" for the three places that number lives).

---

## Repository layout

```
LogLens.Core/          net8.0, NO UI framework. Models, services, view-models.
  Models/              LogLine, HighlightRule + Severity, Workspace (the JSON contract)
  Services/            LogTailer, TimestampParser, RuleSet, SeverityFilter,
                       WorkspaceStore, IssueStore/IssueRecorder/SignatureBuilder,
                       JiraTemplate, RulePresets, PathResolver, UpdateService,
                       AlertPolicy (when to alert), LineFinder (find-in-tab)
  ViewModels/          MainVm owns ViewVm owns panes; LogPaneVm (abstract) is
                       subclassed by LogTab (one file) and MergedTab (one view)
  IUiThread.cs         the only UI-framework dependency, as an interface
  BulkObservableCollection.cs, ObservableObject.cs

LogLens/               net8.0-windows, WPF + WinForms. The flagship app.
  MainWindow.xaml(.cs) the shell: menus, sidebar, tabs, status bar, update handoff
  Controls/LogPane     the virtualized log list (both file tabs and merged use it)
  Dialogs/             Settings, Rules, Issues, Alerts, ViewEdit, Update, Prompt
  Services/            Windows-only: AlertService (delivery: tray balloon + taskbar
                       flash; the decision is Core's AlertPolicy), SoundLibrary,
                       ShellOpen (editor / File Explorer)
  Core/                WpfUiThread, WPF converters, RelayCommand
  Themes/              Dark.xaml, Light.xaml, Controls.xaml (control templates)

LogLens.Avalonia/      net8.0, Avalonia 11.3. The macOS (and future Linux) shell.
                       Binds the same LogLens.Core view-models as WPF. Ten files:
                       Program/App/MainWindow, Dialogs, GridDialogs, Support,
                       ShellOpen (open -t / open -R / xdg-open), DesktopAlerts
                       (osascript banner + afplay; notify-send on Linux).

tests/RuleCheck/       net8.0-windows console app. The entire test suite.

tools/                 PowerShell: test-log generators, icon and splash renderers
packaging/             Info.plist, LogLens.icns, macos-readme.txt for the .app bundle
docs/adr/              Architecture decision records
build.ps1              Windows portable-exe publish
.github/workflows/build.yml   CI + release
```

### Namespaces do not tell you the platform

`LogLens.Services` spans **two assemblies**: `LogLens.Core/Services/*` (cross-platform)
and `LogLens/Services/*` (Windows-only). Same namespace, different projects. Judge
platform-safety by which project a file is in, never by its `namespace` line.

Also note `LogLens/Core/ObservableObject.cs` contains `RelayCommand`, not
`ObservableObject` — the base class lives in `LogLens.Core/ObservableObject.cs`.

---

## The one architectural rule

**`LogLens.Core` must never reference a UI framework.** No `System.Windows.*`, no
Avalonia, no WinForms. This is what lets the WPF and Avalonia shells consume the
*same* view-model objects verbatim instead of drifting apart. Consequences you will
run into:

- **Colours are hex strings**, not `Brush`. `LogLine.Foreground` / `Background` and
  `HighlightRule.Foreground` / `Background` are `string`. Each shell converts and
  caches per rule (`LogLens/Core/Converters.cs`, `LogLens.Avalonia/Support.cs`).
- **Marshalling to the UI thread goes through `IUiThread.Post`** — implemented by
  `WpfUiThread` (Dispatcher.BeginInvoke) and `AvaloniaUiThread` (Dispatcher.UIThread.Post).
  Never reach for `DispatcherTimer` in Core; use `System.Threading.Timer` + `Ui.Post`
  (see `MergedTab`'s flush timer).
- Anything genuinely Windows-only (tray-balloon notifications, `.wav` playback,
  taskbar flashing, Explorer/editor integration) lives in `LogLens/Services/`.

`docs/adr/0001-avalonia-over-maui.md` records why Avalonia and not MAUI. Read it
before proposing a different UI stack.

---

## Build and run

**Nothing here builds on Linux except `LogLens.Core` and `LogLens.Avalonia`.** WPF
(`LogLens`) and the test project (`tests/RuleCheck`, which exercises the Windows-only
`SoundLibrary`) target `net8.0-windows` and require a Windows host.
Linux dev containers for this repo also tend to ship without a `dotnet` SDK at all —
check with `dotnet --version` first, and if you cannot compile or run the checks, say
so plainly rather than reporting a change as verified.

```powershell
# Portable self-contained exe -> dist\SelfContained\LogLens.exe (~63 MB)
.\build.ps1

# ~3 MB exe, needs the .NET 8 Desktop Runtime on the target
.\build.ps1 -Mode Framework

# What CI runs
dotnet build LogLens/LogLens.csproj -c Release -warnaserror
dotnet build LogLens.Avalonia/LogLens.Avalonia.csproj -c Release
dotnet run --project tests/RuleCheck -c Release      # exit 0 = all passed

# Fake traffic to try it against (writes .\testlogs\, gitignored)
.\tools\Write-TestLogs.ps1 -Seconds 120        # generic INFO/WARN/ERROR/FATAL mix
.\tools\Write-NLogTestLogs.ps1 -Seconds 60     # NLog pipe layout + JsonLayout

# Regenerate artwork (GDI+; Windows only). New-MacIcns dot-sources New-AppIcon,
# so the Windows .ico and macOS .icns cannot drift.
.\tools\New-AppIcon.ps1 ; .\tools\New-MacIcns.ps1 ; .\tools\New-SplashImage.ps1
```

The macOS `.app` bundle is assembled only in CI (`build-macos` job in
`.github/workflows/build.yml`); there is no local script for it.

---

## Checks: `tests/RuleCheck`

There is no xUnit/NUnit. The whole suite is one runnable console program
(`tests/RuleCheck/Program.cs`, ~1,600 lines) that prints `PASS` / `FAIL` / `SKIP`
lines and returns 1 if anything failed. Deliberate: you can read the output and see
exactly which real log line classified wrong.

Sections it covers — add to the matching one rather than starting a new pattern:

| Section | Guards |
|---|---|
| NLog pipe / JsonLayout / Serilog presets | each preset classifies real lines correctly |
| Default rules, level field vs message keywords | `\|INFO\|…transient error` must stay INFO |
| Multi-line events | continuation lines cannot declare severity |
| Pipe-format detection | `RuleSet.LooksPipeLevelled` sampling |
| Timestamp detection | format picking, time-only midnight rollover |
| Merged timeline ordering | a real `MergedTab`: watermark release, tie-breaks, late-arrival re-sort, reseed on rewind |
| Alert decisions / sounds | `AlertPolicy.Decide` outcomes, throttling, shared wording, Mac sound fields, `SoundLibrary` resolution |
| Find in tab | `LineFinder`: plain/regex/case, stepping from the selection, wrap, re-scan on growth |
| Severity chip filter | carry semantics for unclassified lines |
| Workspace compatibility | **a v1.1.0-shaped workspace must still load losslessly**; portable vs per-user location |
| Legacy default-rule upgrade | untouched old defaults upgrade; edited lists never do |
| Continuation semantics | the settled-verdict gate |
| Self-update | version/checksum parsing, local-server download + verify (mismatch and missing checksum refused), the rename swap, leftover cleanup, predecessor wait |
| Tailer | CRLF and UTF-8 across the 64 KB read boundary, partial-line hold-back, truncate-and-rewrite, wildcard roll-over, rename-rotation |
| Issue signatures / database / migration | each mask pinned, grouping stability, upsert, per-application filter, v1→v2 schema move |

Helpers: `Expect(ruleSet, line, expectedSeverity, description)`,
`ExpectRule(...)` when the *rule name* matters, `Report(ok, what, detail)`,
`Skip(what, why)` for machine-dependent checks (a skip is loud but not a failure).

**Any bug fix in classification, tailing, merging, the workspace format, or the
issue database needs a case here.** That is the established pattern in this repo's
history — nearly every "Fix what the review confirmed" commit adds one.

---

## Releasing

1. Bump the version in **all three** project files — they are currently kept in
   lockstep at `1.5.4`:
   - `LogLens/LogLens.csproj` (`<Version>` *and* `<InformationalVersion>`) — this is
     the one `UpdateService.CurrentVersion` reads from the entry assembly and the
     About box shows.
   - `LogLens.Core/LogLens.Core.csproj` (`<Version>`)
   - `LogLens.Avalonia/LogLens.Avalonia.csproj` (`<Version>`, `<InformationalVersion>`)
2. Move `CHANGELOG.md`'s **Unreleased** section under the new version and date.
3. `git tag -a v1.5.5 -m "what changed" && git push origin v1.5.5`
4. Once the release exists, point the package manifests at it: `bucket/loglens.json`
   (`version`, `url`, `hash` — Scoop's `checkver.ps1 -u` fills them from the
   `autoupdate` block) and a new `packaging/winget/manifests/…/<version>/` folder
   (`wingetcreate update JollyKrampus.LogLens -v <version> -u <exe url>` writes
   it; submitting to `microsoft/winget-pkgs` is a manual PR). Hashes come from the
   release's `SHA256SUMS.txt`, never from a local build.

CI does the rest: three jobs — `build-windows` (always builds + runs checks; publishes
the exe and `SHA256SUMS.txt` only on a tag or manual run), `build-macos` (tag/manual
only; publishes `LogLens-macos-osx-arm64.tar.gz` and `-x64`, each containing a
`LogLens.app`), and `release` (tag only; downloads both artifacts and creates the
GitHub release with generated notes). A tag containing `-` publishes as a prerelease.

Need a binary without tagging: Actions tab → **build** → *Run workflow*.

The macOS version stamp keys on `GITHUB_REF_TYPE == "tag"`, not on a name prefix —
a manual dispatch from a branch called `version-bump` would otherwise stamp
`ersion-bump`. Do not "simplify" that condition.

---

## Invariants — each of these was a real bug

### Build / publish

- **Never set `InvariantGlobalization`.** WPF's `XmlLanguage.GetSpecificCulture()`
  needs real ICU data; without it every binding throws during layout.
- **Never enable `PublishTrimmed`.** WPF is not trim-safe — trimming breaks XAML
  reflection at *runtime*, not at build time.
- **Keep `IncludeNativeLibrariesForSelfExtract=true`** on both platforms. Without it
  the publish drops `e_sqlite3` / Skia / HarfBuzz / AvaloniaNative beside the binary
  and "one self-contained file" stops being true.
- WinForms is referenced **only** for the colour and font pickers. Its implicit
  usings collide with WPF (`Brush`, `FontFamily`, `Timer`, `UserControl`), so
  `System.Windows.Forms` and `System.Drawing` are `<Using Remove=…>`'d in the csproj
  and those types are referenced by full name. Don't re-add the usings.
- Only the WPF build runs with `-warnaserror` in CI, but keep everything warning-free.

### Highlight rules and severity

- **First match wins, top to bottom** (BareTail semantics). `FATAL` must sit above
  `ERROR`, or every fatal is claimed by the error rule.
- **The level field outranks message keywords.** `HighlightRule.Defaults()` is two
  tiers: anchored `(^|\|)\s*(LEVEL)\s*\|` rules first, loose `\bLEVEL\b` keyword
  rules below. That is what keeps
  `…|INFO|…|Recovered from a transient error…` an INFO line. Never reorder the tiers,
  and keep the two tiers' level vocabularies in sync — a level the keyword tier knows
  but the field tier doesn't (e.g. `ERR`, `INFORMATION`) falls through and message
  keywords win again.
- Known trade-off, already accepted: a level in the *last* column can't be told apart
  from a message ending in that word, so those layouts fall back to keyword matching.
- **Exception-continuation rules sit above the keyword tier** with `Severity.None`,
  so a stack frame or exception header that happens to say "fatal" reads as detail of
  its parent event, not a fresh FATAL. In the defaults the inner-exception rule is
  anchored (`^\s*(--->|--- End of inner exception)`) — a bare `--->` substring would
  swallow real `ERROR … ---> IOException` event lines into `None`.
- Bad regexes are **inert, never fatal**: compile failures are cached, matches have a
  100 ms timeout, and a timeout returns "no match". Keep it that way — these run per
  line at hundreds of lines a second.

### Ingestion and continuation semantics

- A line with **no timestamp of its own, in a file whose lines carry them**, is a
  *continuation* (`LogLine.IsContinuation`). Continuations inherit the timestamp above
  them and are skipped by loose severity rules (`RuleSet.Match(line, isContinuation)`).
- That gate is `TimestampExtractor.HasSettledFormat` — a **settled verdict with a
  format** — never the provisional guess. Any warm-up line that merely embeds a date
  in its message sets the provisional guess; using it once turned entire keyword-only
  files severity-less (no colours, no chips, no alerts, no issues).
- `ObserveForDetection` is the **only** thing that feeds detection sampling, and
  `LogTab.Ingest` calls it for a whole batch *before* classifying any of it. Sampling
  in `Read` as well double-counted head-of-batch lines and flipped the verdict.
  Do not re-add sampling to `Read`.
- `Rebuild()` re-resolves rules on every line so rule edits show immediately — it must
  keep passing `l.IsContinuation`, or a filter change re-promotes spill lines.
- `LogTailer` opens with `FileShare.ReadWrite | Delete` (the `Delete` share is what
  lets a rotating logger rename its own file), **polls** rather than using
  `FileSystemWatcher` (watcher events are unreliable on SMB and buffered writes),
  keeps a stateful `Decoder` across reads, and holds back a trailing partial line
  until its newline arrives. A `\r\n` straddling the 64 KB buffer boundary is handled
  by `_pendingCr`. On truncation it must clear `_primed` along with the decoder — not
  doing so null-dereferenced on the next read.

### Merged timeline

- Files are polled independently, so lines arrive out of order. `MergedTab` uses a
  **watermark**: lines sit in a holding buffer until older than `MergeWindowMs`
  (1 s default), then flush as a sorted append. A source stalling past the window is
  detected and repaired with a full `ResortAll()`, and the status bar says so.
  Do not replace this with sort-on-every-batch — that is O(n log n) several times a
  second at 200k lines.
- Sort key is always `(Timestamp, SourceIndex, Number)`. The last two make ties
  deterministic; millisecond timestamps tie constantly.
- `Held` stores the owning **tab**, not a pre-stamped line: source name/colour/index
  are read at release time, because adding or removing a tab re-indexes sources.
  Any add/remove calls `Reseed()` for the same reason.
- Sidebar badges aggregate over `FileTabs` only — the merged tab holds copies of the
  same lines and would double every count.
- The severity-chip carry is keyed by `SourceIndex` (`SeverityCarryMap`): a single
  carry would attach file A's stack trace to file B's interleaved INFO line.

### Workspace format — a compatibility contract

`loglens.workspace.json` is shared between teammates across versions, so:

- New settings must be **additive with sensible defaults**. Old workspaces load in
  new versions unchanged; unknown fields from a newer version are ignored, not fatal.
  `CheckWorkspaceCompat()` loads an embedded v1.1.0-shaped workspace — including an
  unknown field from a hypothetical future version — and fails the build if anything
  is lost or throws.
- `Workspace.CurrentVersion` (currently **3**) is bumped only when the *default rules
  change shape*. The property default stays `1` so a field-less old file deserialises
  as v1.
- The one-time legacy rule upgrade in `WorkspaceStore.Load` runs only when
  `ws.Version < CurrentVersion` **and** the rule list matches a previous default set
  exactly, field for field. Without the version gate, a user who deleted some default
  rules would see them resurrected on every load. When the defaults change again:
  bump `CurrentVersion` **and** append the outgoing shape to `LegacyDefaultSets`.
- Saves are write-to-`.tmp`-then-`File.Replace`, so a crash mid-save can't truncate.

### Issue database

- Primary key is **`(hash, view)`** — the same signature in Dev and Prod is two rows
  with independent counts, Jira keys and ignore flags. This is deliberate, not an
  oversight.
- Databases from ≤1.3 keyed on `hash` alone; `MigrateFromV1` carries every row over.
  It drops v1's indexes **first**, because SQLite index names are schema-wide and a
  surviving `ix_issues_count` silently no-ops the new `CREATE INDEX IF NOT EXISTS`.
- Grouping is **deterministic regex only** — `SignatureBuilder` masks timestamps,
  GUIDs, IPs, paths, URLs, emails, hex (long, `0x`, and short ids with a digit and
  a letter), quoted strings, numbers, and digit runs inside lowercase names
  (`node7` → `node<n>`; `Int32` is left alone). Changing a mask changes the hash
  of every stored issue it touches, which strands their counts and Jira keys — so
  the masks are pinned one by one in RuleCheck, and a change needs a changelog line. No model, no
  network, no per-line cost. A grouping that drifts is worse than one that is merely
  good; do not "improve" this with inference.
- Writes are queued and flushed on a 2 s background timer, capped at 50,000 queued.
  Log ingestion must never wait on a disk write, and a runaway log must not eat memory.
- On upsert, the **latest classification wins** for `severity` (so a rule fix
  re-classifies stored rows) and the **richest sample wins** (one with a stack trace
  beats one without).
- LogLens never talks to Jira. It formats a ticket (`JiraTemplate`) and you paste it.

### Self-update (Windows)

- The swap exploits a Windows quirk instead of a helper script: a running exe can't be
  overwritten but *can* be renamed. `LogLens.exe` → `LogLens.exe.old`, verified
  download → `LogLens.exe`, start it, exit; next start deletes the `.old`. If the
  `.old` is locked, `PerformSwap` steps aside to `.old-<ticks>` rather than failing.
- The download is **always verified against the release's `SHA256SUMS.txt`** before
  anything is touched. Never skip this.
- Handoff protocol: `MainWindow.PrepareForUpdateHandoff()` saves the workspace and
  disposes the issue store *before* the successor starts; the successor is passed
  `--updated-from <pid>` and `UpdateService.WaitForPredecessor` blocks (bounded, 15 s)
  until the old process exits. Both shells call `WaitForPredecessor` and
  `CleanUpLeftovers` at startup even though only WPF can *initiate* an update.
  `AbortUpdateHandoff()` must restore auto-save and the issue recorder if the swap
  fails, or every later change is silently lost.
- `UpdateService`'s `HttpClient` is `Lazy` on purpose: an eager field initialiser ran
  before `CurrentVersion`'s and died in the type initializer.
- Version is read from the **entry assembly**, not the executing one — this code lives
  in `LogLens.Core` now and Core's own version is irrelevant.

### macOS

- A `.app` bundle is **not** a portable install. `WorkspaceStore.ResolveDefaultPath`
  refuses to treat `Contents/MacOS` as the portable location; the workspace goes to
  `~/.config/LogLens/`. Writing inside the bundle would hide it and break signing.
  The same rule covers a `loglens.not-portable` marker beside the exe, which the
  Scoop manifest creates: Scoop gives each version a new folder, so a workspace
  kept beside the exe was stranded on every `scoop update`.
- Binaries are unsigned/un-notarised; users run `xattr -cr LogLens.app` once. The CI
  has a disabled codesign step ready for the day an Apple account exists.
- Mac alert text comes from log lines, so it is an injection surface. `DesktopAlerts`
  passes it to `osascript` as **argv** against a constant script (read by negative
  index, so a passed-through `--` can't shift it) — never interpolate it into
  AppleScript source. Sound names are matched against the system-sound list, never
  used as a path.
- Mac sounds are **separate** `AlertSettings` fields (`MacSoundName`,
  `MacFatalSoundName`), not a reinterpretation of `SoundName`: a workspace shared
  between a Windows and a Mac teammate must keep both choices.

---

## Code conventions

- **C# 12 / .NET 8**, `Nullable` and `ImplicitUsings` enabled everywhere,
  `LangVersion latest`. Collection expressions (`[]`), file-scoped namespaces,
  primary-constructor-free `sealed` classes, `record` for value-ish payloads
  (`TailBatch`, `IssueFingerprint`, `IssueOccurrence`, `LinesAppendedEventArgs`).
- **`sealed` by default**; `ObservableObject` and `LogPaneVm` are the only
  intentional abstract bases.
- **MVVM without a framework.** `ObservableObject.Set(ref field, value)` /
  `Raise(nameof(X))`; `RelayCommand` is the only ICommand. Do not add MVVM Toolkit,
  ReactiveUI, or a DI container — the app is deliberately dependency-light
  (`Microsoft.Data.Sqlite` and Avalonia are the only NuGet packages).
- **XML doc comments carry the reasoning**, not the signature. The house style is a
  `<summary>` that explains *why this shape and not the obvious one*. Match it.
- **Failures degrade, they don't throw.** A bad regex matches nothing; a missing sound
  file beeps; a locked workspace waits for the next auto-save tick; issue tracking
  swallows its own errors because it must never disturb tailing. `App.LogError` writes
  to `%TEMP%\loglens-errors.log`.
- Hot paths (per line, per batch) avoid allocation and LINQ; cold paths use whatever
  reads best.
- `.gitattributes` normalises to LF, except `*.ps1` which is CRLF. Don't fight it.
- Never commit `dist/`, `testlogs/`, `bin/`, `obj/`, or any `*.workspace.json`.

---

## Where runtime state lives

| File | Location | Notes |
|---|---|---|
| `loglens.workspace.json` | beside the exe if writable and no `loglens.not-portable` marker, else `%APPDATA%\LogLens` (macOS: `~/.config/LogLens`) | views, rules, settings, per-pane chips/filters, window placement |
| `loglens.issues.db` | beside the workspace | SQLite, WAL mode; absent entirely if **Track issues** is off |
| `loglens-errors.log` | `%TEMP%` | crash/handled-error log |
| `LogLens.exe.old` | beside the exe | self-update leftover, deleted on next start |

Portable-first is a product identity, not an implementation detail: the app must keep
working when copied to a USB stick or a jump box with nothing installed.

---

## Two shells, one core

When you change behaviour, decide which layer it belongs to:

- **Logic** → `LogLens.Core`. Both shells get it for free; add a `RuleCheck` case.
- **Windows-only chrome** → `LogLens/`. Self-update (UI and the rename swap), the
  tray balloon, taskbar flash and `.wav` library are Windows-only *by design*
  (`docs/adr/0002-macos-parity-scope.md` says why self-update stays that way).
- **Avalonia parity** → `LogLens.Avalonia/`. It is deliberately thin (10 source files) and
  binds the same view-models. Find-in-tab, editor/Finder integration and alerts are
  ported; what remains different is deliberate: no self-update, no taskbar flash,
  and clicking a notification can't jump to its view. Closing a gap is a port, not a
  redesign — and if the port needs logic that lives in WPF code-behind, move that
  logic to Core first (as `AlertPolicy` and `LineFinder` were).

If you add a view-model property the WPF XAML binds to, check whether the Avalonia
XAML should bind it too — silent divergence between the shells is the main risk the
Core split exists to prevent.

---

## Commits and PRs

Observed convention in this repo's history:

- Subject line is a **sentence describing the change's effect**, not a conventional-commit
  prefix: *"Continuation lines cannot declare severity via loose keywords"*,
  *"Save before the swap, and let the level field outrank message keywords"*.
- Body explains **what was wrong and why the fix is shaped that way**, in prose
  paragraphs, wrapped ~72 chars. Bug-fix commits state how many review findings were
  confirmed vs refuted.
- Work happens on a branch and lands via PR; `main` is the release branch and tags
  are cut from it.

---

## Keeping the docs honest

`README.md` is the user manual and describes current behaviour. `docs/adr/` records
decisions **as they were made** — it is a history, not a status page. When you change
behaviour the README documents, update it in the same commit.

For an ADR, never rewrite an accepted decision to match new reality: append a dated
update section instead, the way `docs/adr/0001` does for the 1.5.3 macOS bundle. The
original argument is the record; what changed since goes underneath it, saying plainly
whether the decision itself still stands.

Comments that explain a *reason* go stale silently when the reason moves, and this
codebase leans on its comments harder than most. `tests/RuleCheck/RuleCheck.csproj`
carried one blaming WPF brushes long after `HighlightRule` became hex strings in Core.
If you find another, correct it rather than working around it — a comment that explains
the wrong thing is worse than none, because it will be believed.
