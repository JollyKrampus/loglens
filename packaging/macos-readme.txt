LogLens for macOS
=================

LogLens.app is the whole app — self-contained, nothing to install: the .NET
runtime and the UI toolkit are baked in, the same way the Windows exe works.
(On first launch it unpacks its native pieces to a temporary folder; that is
normal.)

First launch has one extra step because the app is not yet code-signed, so
macOS quarantines anything downloaded from a browser. In Terminal, from the
folder you extracted this into:

    xattr -cr LogLens.app

then double-click LogLens.app like any other app. After that first time it
opens normally, with its own icon in the Dock.

Which download is which:
  Apple Silicon (M1/M2/M3/M4)  ->  LogLens-macos-osx-arm64.tar.gz
  Intel Macs                   ->  LogLens-macos-osx-x64.tar.gz

Your workspace is saved to ~/.config/LogLens/loglens.workspace.json (an app
bundle is not a portable install, so nothing is written inside LogLens.app).
If you used a pre-1.5.3 build, copy the loglens.workspace.json that sat next
to the old binary into that folder to keep your views.

Early build: live tailing, views, the merged timeline, highlighting and the
issue database all work. Alerts, sounds and self-update are Windows-only for
now; grab new versions from
https://github.com/JollyKrampus/loglens/releases
