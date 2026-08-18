# ADR 0001: Avalonia over .NET MAUI for the cross-platform UI

**Status:** Accepted (implemented in 1.5.0)
**Date:** 2026-08-18
**Context:** [issue #1](https://github.com/JollyKrampus/loglens/issues/1) — macOS support

## Context

LogLens started as a WPF app. macOS support meant choosing a second UI stack,
and the two serious .NET candidates were **Avalonia** and **.NET MAUI**. The
constraints that mattered:

- LogLens is a **desktop power tool**: virtualized lists holding tens of
  thousands of monospace rows, per-row coloring, dense keyboard/mouse
  interaction, context menus, modal dialogs, multi-pane layouts.
- The port had to **reuse the existing view-models**. By the time of the port,
  all of them lived in `LogLens.Core` behind an `IUiThread` abstraction; the
  cheaper the UI layer's divergence from WPF, the less risk of the two apps
  drifting apart behaviourally.
- Distribution is **portable-first**: one self-contained file you drop on a
  jump box, no installer, no runtime prerequisites — the identity of the
  product since 1.0.
- CI runs on free GitHub Actions minutes; build complexity is a recurring tax.

## Decision

Avalonia.

## Reasons

1. **MAUI's macOS story is Mac Catalyst.** MAUI has no native desktop toolkit
   for the Mac: it renders through Catalyst, an iOS-derived stack, with desktop
   idioms (menus, dense lists, precise scrolling, window management) as
   second-class citizens. Avalonia draws its own pixels via Skia and treats
   desktop as the primary target. For a log viewer, list virtualization
   performance alone is decisive — MAUI's `CollectionView` on desktop is not
   built for 100k-row monospace grids; Avalonia's virtualized `ListBox` is the
   same design as WPF's.

2. **WPF affinity made the port mechanical.** Avalonia's XAML, binding system,
   data templates, and control vocabulary track WPF closely. The shared
   view-models in `LogLens.Core` were consumed **verbatim** — the Avalonia shell
   binds to the exact objects the WPF shell binds to. A MAUI port would have
   meant re-expressing the UI in a different idiom (handlers over native
   controls, a different layout system, a different lifecycle), multiplying the
   surface where the two apps could disagree.

3. **Packaging matches the product's identity.** `dotnet publish` with
   `--self-contained -p:PublishSingleFile=true` gives one bare executable per
   architecture — the exact analogue of the Windows exe, tarred and shipped.
   Catalyst produces a `.app` bundle wired into Apple's packaging expectations;
   unsigned distribution (all we can do without a paid Apple account) is
   markedly more awkward for a bundle than for a single Mach-O file.

4. **CI stays boring.** Avalonia builds with the plain .NET SDK on any runner.
   MAUI needs `dotnet workload install maui` (minutes of extra install per run,
   a known source of CI flakiness) and pins to Xcode versions on the macOS
   runner. Our macOS job is nine lines long.

5. **Linux is free.** Avalonia runs on Linux; MAUI does not target it. If the
   team ever tails logs on a Linux jump box, the same binary approach extends
   there with one more RID in the build matrix.

## Trade-offs accepted

- **Not Microsoft-supported.** Avalonia is third-party OSS (MIT, with a
  commercial company behind it). MAUI carries Microsoft's LTS promise. We judged
  Avalonia's desktop track record more valuable than the support label, given
  MAUI's desktop investment visibly trails its mobile focus.
- **No native controls.** Avalonia draws everything, so the app doesn't feel
  Mac-native (no native menu behaviours out of the box, custom accessibility
  path). The WPF original isn't native-feeling either — LogLens has its own
  visual identity — so consistency across platforms was worth more than
  platform mimicry.
- **No mobile path.** If LogLens ever wanted an iOS/Android companion, MAUI
  would have been the head start. A log tailer on a phone is not a product we
  can imagine wanting.

## Consequences

- The Avalonia app shares `LogLens.Core` (models, services, view-models)
  verbatim; only the XAML shell and dialogs are per-framework.
- Feature gaps in the Avalonia shell (alerts/sounds, self-update, find-in-tab,
  editor integration) are ports-not-designs: the logic is already in Core or is
  Windows-specific by nature.
- Windows remains the flagship: WPF ships as the `LogLens.exe` everyone uses;
  the Avalonia app is the macOS (and future Linux) vehicle.
