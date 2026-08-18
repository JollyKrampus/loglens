LogLens for macOS
=================

The LogLens file in this folder is the whole app — one self-contained
executable. There is nothing to install: the .NET runtime and the UI toolkit
are baked into the binary, the same way the Windows exe works. (On first
launch it unpacks its native pieces to a temporary folder; that is normal.)

First launch has two extra steps because the app is not yet code-signed, so
macOS quarantines anything downloaded from a browser. In Terminal, from the
folder you extracted this into:

    xattr -cr .
    chmod +x LogLens
    ./LogLens

After that first run, double-clicking LogLens in Finder works too.

Which download is which:
  Apple Silicon (M1/M2/M3/M4)  ->  LogLens-macos-osx-arm64.tar.gz
  Intel Macs                   ->  LogLens-macos-osx-x64.tar.gz

The workspace file (loglens.workspace.json) is created next to the binary,
same as on Windows — so keep LogLens in its own folder.

Early build: live tailing, views, the merged timeline, highlighting and the
issue database all work. Alerts, sounds and self-update are Windows-only for
now; grab new versions from
https://github.com/JollyKrampus/loglens/releases
