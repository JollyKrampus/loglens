# LogLens

A portable real-time log viewer for Windows 11. Built as a BareTail replacement:
same instant tail-and-highlight feel, plus the things BareTail never had — saved
views for log groups, live filtering, wildcard paths that follow rolling files,
and at-a-glance error counts per environment.

No installer, no service, no account, no telemetry, no licence.

---

## Getting it running

```powershell
.\build.ps1
```

That produces a single self-contained executable in `dist\SelfContained\LogLens.exe`
(~63 MB). Copy it anywhere — a USB stick, a jump box, a network share — and run it.
Nothing needs to be installed on the target machine.

If the machines you care about already have the **.NET 8 Desktop Runtime**, you can
build a ~3 MB executable instead:

```powershell
.\build.ps1 -Mode Framework
```

To try it without touching real logs, generate some fake traffic:

```powershell
.\tools\Write-TestLogs.ps1 -Seconds 120
```

That writes `dev-app.log`, `test-app.log` and `prod-app.log` into `.\testlogs\`
with a realistic mix of INFO/DEBUG/WARN/ERROR/FATAL and the odd stack trace.

---

## The idea: views

A **view** is a saved group of log files — `Dev`, `Test`, `Prod`, or
`Order Service`, `Payments`, `Nightly Batch`, however you think about it. Each
view has its own tabs, its own colour stripe in the sidebar, and its own optional
highlight rules.

The sidebar shows a red badge with the error count for each view, so a glance at
the left edge tells you which environment is unhappy without clicking into it.

Everything — views, files, rules, filters, fonts, window position — lives in a
single `loglens.workspace.json`. You can keep several: one per project, one per
incident, one you hand to a colleague. `File ▸ Save workspace as…`.

**Where it's stored:** next to the .exe if that folder is writable (so the whole
thing stays portable), otherwise `%APPDATA%\LogLens`. The status bar always shows
the path in use.

---

## Day-to-day

| | |
|---|---|
| **Follow** | Auto-scrolls as lines arrive. Scrolling up switches it off; scrolling back to the bottom switches it on again. |
| **Pause** | Holds incoming lines without dropping them. Unpause and they all appear. |
| **Clear** | Empties the on-screen buffer. Your log file is never written to. |
| **Show / Hide** | Live include/exclude filters. `.*` treats them as regex, `Aa` makes them case-sensitive. |
| **Find** | `Ctrl+F`. Enter for next, Shift+Enter for previous, with a match count. |

Drag log files onto the window to add them to the current view.

### Keyboard

```
Ctrl+F   Find in the current tab
Ctrl+S   Save the workspace
Ctrl+O   Open a workspace
Ctrl+C   Copy the selected lines
Ctrl+A   Select all
```

---

## Merged timeline

`Files ▸ Merged timeline for this view` adds a **Merged** tab that interleaves every
file in the view into one time-ordered stream, with a colour-coded column showing
which file each line came from.

Timestamps are detected automatically — ISO 8601, NLog's `${longdate}`, log4net's
comma milliseconds, JSON `time`/`@t` fields, syslog, and Serilog's time-only console
format. You are never asked to describe your layout.

**Lines with no timestamp of their own inherit the one above them.** That is what
keeps a stack trace sitting underneath the error that threw it instead of scattering
across the timeline.

### How live merging stays correct

Files are polled independently, so file B's 12:00:01 line can easily arrive before
file A's 12:00:00 line. Re-sorting the whole buffer on every batch would be
O(n log n) several times a second, which is not viable at 200k lines.

Instead LogLens uses a **watermark**, the trick stream processors use: incoming lines
sit in a holding buffer and are only released once they are older than the merge
window (1 s by default). Every file is polled well inside that window, so by release
time nothing earlier can still be coming, and the flush is a plain sorted append.

If a file stalls for longer than the window it breaks that assumption — so that case
is detected and repaired with a full re-sort, and the merged tab's status bar tells
you it happened. If you see that often, raise **Merged timeline delay** in Settings.

---

## Alerts

`Alerts` menu, or `Alerts ▸ Alert settings…`.

Tells you something broke while you were looking elsewhere: a notification, a sound,
and a taskbar flash. Clicking the notification jumps to the view that raised it.

| Setting | Why it's there |
|---|---|
| **Alert at this level or above** | Error by default; drop to Warn if you want more. |
| **Also alert on this pattern** | Regex that fires at *any* level — a specific error code, a customer id. |
| **At most one alert every N seconds, per view** | A service failing hard emits thousands of errors a second. This is what stops that becoming thousands of notifications. |
| **Only when LogLens is not the active window** | No point shouting about a line you're already reading. |
| **Alerts for this view** | Mute dev and test, stay alert to prod. |
| **Sound** | Pick from the Windows sound library — grouped into System, Recommended and everything else — or Browse for your own `.wav`. Selecting one plays it, and the ▶ button replays it. |
| **Use a different sound for FATAL** | On by default, so a fatal is audibly distinct from a routine error without having to look. |

Sounds default to *Windows Notify* for errors and *Windows Critical Stop* for
fatals. A missing or unreadable file degrades to a plain beep rather than
throwing — an alert is the worst possible moment to raise a second error.

`Alerts ▸ Send a test alert` fires a sample so you can confirm it actually reaches
you before you rely on it.

Notifications use a tray-icon balloon rather than a WinRT toast on purpose: WinRT
toasts need a registered AppUserModelID and a packaged identity, which a portable
single .exe copied onto a jump box does not have.

---

## Log formats

`Tools ▸ Highlight rules ▸ Load preset…` ships four rule sets:

| Preset | For |
|---|---|
| **Severity keywords (generic)** | Anything. Matches `FATAL`/`ERROR`/`WARN`/`INFO`/`DEBUG` anywhere on the line. |
| **NLog / log4net (pipe-delimited)** | `${longdate}\|${level:uppercase=true}\|${logger}\|${message}` — NLog's stock file layout. |
| **NLog JsonLayout** | NLog's `JsonLayout`, one JSON object per line. |
| **Serilog / short levels** | Three-letter levels: `VRB DBG INF WRN ERR FTL`. |

### Why the NLog preset instead of the generic one

The generic preset matches keywords anywhere on the line, so this real line gets
mis-coloured as an error:

```
2026-08-14 12:52:40.0835|INFO|Acme.Jobs.NightlyBatch|Recovered from a transient error after 3 attempts
```

It's an INFO line, but the word "error" is in the message, and `Error` is checked
before `Info`. The NLog preset anchors on the level being **its own pipe-delimited
field**:

```
(^|\|)\s*(ERROR)\s*\|
```

so the message text can say whatever it likes. That shape also means it doesn't
care whether the level is the second column or the fifth — reordering your NLog
layout won't break it.

### Multi-line exceptions

`${exception:format=tostring}` spills across several lines that carry no level
field:

```
2026-08-14 12:52:44.1692|ERROR|Acme.Orders.OrderService|Timeout calling payments
System.Net.Http.HttpRequestException: The operation timed out.
 ---> System.TimeoutException: A task was canceled.
   at Acme.Payments.PaymentClient.ChargeAsync(ChargeRequest r)
   --- End of inner exception stack trace ---
```

The NLog preset colours all of it so an exception reads as one block, but those
continuation lines are set to **count as None** — otherwise a single exception
would register as six errors and the sidebar badge would lie to you.

### Checking the presets

```bash
dotnet run --project tests\RuleCheck
```

That classifies known-tricky lines against every preset and prints PASS/FAIL —
including the "INFO whose message says error" case above. Add a case there if you
hit a line that colours wrong.

To generate NLog-shaped traffic to try it against:

```powershell
.\tools\Write-NLogTestLogs.ps1 -Seconds 60
```

---

## Highlighting

`Tools ▸ Highlight rules`. Rules are checked **top to bottom and the first match
wins**, exactly like BareTail — so keep `FATAL` above `ERROR`, or every fatal line
will be claimed by the error rule.

Each rule has a pattern (plain substring or regex), text and background colours,
a bold flag, and a **Counts as** severity. That severity is what drives the
per-tab counters and the sidebar error badges, so a custom rule like
`OrderRejected` set to *Error* will show up in the badge too.

The panel at the bottom of the dialog lets you paste a real log line and see
which rule claims it, before you commit.

View-specific rules are checked before global ones, so a single view can override
a global rule without affecting the others.

---

## Rolling files

A path may contain `*` or `?`:

```
C:\logs\app-*.log
\\prod-app01\logs\service-????-??-??.log
```

LogLens tails whichever match was written most recently, and when tomorrow's file
appears it follows the roll on its own — no re-pointing the tab at midnight.

It also handles the other rotation styles: if the file is truncated in place or
replaced, the tab resets and picks up the new content instead of going silent or
showing garbage.

---

## Settings worth knowing

`Tools ▸ Settings`:

- **Check for new lines every** — 250 ms by default. Raise it for logs on a slow
  network share; lower it if you want it even snappier locally.
- **Load at most N KB when opening** — 2 MB by default, so opening a 4 GB log is
  instant. Set to 0 to load the whole file.
- **Keep at most N lines per tab** — 200,000 by default. Oldest lines drop off the
  top, which is what keeps memory flat on a log that runs for days.

---

## How it works

Notes for whoever maintains this next.

**Reading.** `Services\LogTailer.cs` opens each file with
`FileShare.ReadWrite | Delete`. The `Delete` share is the important one: without
it, a logger that rotates by renaming its own file fails while LogLens has it
open. It polls on a timer rather than using `FileSystemWatcher`, because watcher
events are unreliable on SMB shares and on buffered writes — precisely where
production logs live. A stateful `Decoder` is kept across reads so a multi-byte
character split across a chunk boundary doesn't turn into garbage, and a trailing
partial line is held back until its newline arrives so you never see a half-written
line flicker and change.

**Rendering.** `Controls\LogPane.xaml` uses a virtualizing `ListBox` in pixel
scroll mode with container recycling, so only the visible rows exist as visuals.
Per-line colour lives on the `LogLine` and is applied through the item container,
which is why 200k lines scroll smoothly.

One subtlety worth preserving: scrolling to the tail uses `ScrollIntoView` on the
last item *and then* `ScrollToEnd`. `ScrollToEnd` alone undershoots, because with
virtualization the scroll extent is an estimate derived from realised containers
and "the end" moves as rows materialise.

**Threading.** Tailers run on threadpool timers and marshal batches to the UI
thread. Appends are batched, and scroll-to-tail requests are coalesced to one per
frame so a firehose log doesn't starve the dispatcher.

### Two traps, documented so they aren't reintroduced

- **Do not set `InvariantGlobalization`.** WPF's `XmlLanguage.GetSpecificCulture()`
  needs real ICU culture data. Without it, every data binding throws during layout.
- **Do not enable `PublishTrimmed`.** WPF is not trim-safe; trimming breaks XAML
  reflection at runtime rather than at build time.

WinForms is referenced solely for the standard colour and font pickers. Its
implicit usings collide with WPF's (`Brush`, `FontFamily`, `Timer`, `UserControl`),
so they're removed in the .csproj and those two types are referenced by full name.

---

## What it doesn't do

Being straight about the gaps:

- **Alerts are local only.** It notifies *you*, on *this* machine, while it's
  running. It will not email, page, or post to Slack — if you need that, you need a
  server-side tool, not a desktop one.
- **Not a big-file search engine.** It tails the last 2 MB and caps at 200k lines by
  design. If you need to grep a 10 GB archive, use [klogg](https://github.com/variar/klogg)
  — it indexes multi-gigabyte files with SIMD and will beat this comfortably.
- **No column parsing.** LogExpert's columnizers split a line into sortable fields;
  LogLens treats a line as text plus a timestamp.
- **No Windows EventLog.** [SnakeTail](https://github.com/snakefoot/snaketail-net)
  does that.
- **No structured/JSON log parsing.** Lines are treated as text. The JsonLayout
  preset colours by level, but there's no field extraction, no querying by
  property, and no collapsing of a JSON line into columns. If you want that,
  ship NLog to Seq instead — see below.
- **A final line with no trailing newline is held back** until the newline arrives.
  This is deliberate — it prevents half-written lines flickering — but if you have
  a writer that never terminates its last line, you won't see it until it does.

### Honest positioning

Merged timelines are not novel. [lnav](https://lnav.org/features) has done it for
years with 70+ auto-detected formats and SQL over your logs, and Microsoft's CMTrace
has a "Merge selected files" option. What is genuinely missing from the portable
Windows GUI tools is **saved, named views with per-environment error badges** — the
glance-at-the-sidebar-and-see-prod-is-angry workflow. That's what this is for.

---

## When to outgrow this

LogLens is a live-tail tool. It is the right thing for "what is happening right
now on that box" and the wrong thing for "how often did this fail last quarter".

For a .NET/NLog shop that can't get budget or platform-ops help, the highest-value
next step is **[Seq](https://datalust.co/pricing)**: a structured-log server you
run yourself as a Windows service or container. NLog has a first-party target
([`NLog.Targets.Seq`](https://github.com/datalust/nlog-targets-seq)), so it's a
config change rather than a code change, and it's free for single-user use.
It gives you search across all history, querying by structured property, and
dashboards — the things a tail viewer fundamentally can't.

The two tools coexist well: Seq for history and search, LogLens for watching a
file live while you reproduce something.

---

## Regenerating the icon

```powershell
.\tools\New-AppIcon.ps1
```

Redraws `LogLens\app.ico` at 16/20/24/32/48/64/128/256 px and writes a preview
sheet to `docs\app-icon-preview.png` that includes 6x nearest-neighbour blow-ups
of the small sizes — the only honest way to tell whether 16 px still reads.

Sizes at and below 32 px use deliberately chunkier geometry: strokes scaled down
from the 256 px artwork land under a pixel and dissolve into grey mush.
