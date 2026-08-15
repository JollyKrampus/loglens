using System.IO;
using System.Media;

namespace LogLens.Services;

/// <summary>One selectable alert sound.</summary>
/// <param name="Id">
/// Either "system:Name" for a Win32 system sound, a bare file name resolved against
/// %WinDir%\Media, or an absolute path to any .wav the user picked.
/// </param>
public sealed record AlertSound(string Id, string Display, string Group)
{
    public override string ToString() => Display;
}

/// <summary>
/// The sounds offered in the alert settings dropdown.
///
/// System sounds are listed first because they always exist and honour whatever the
/// user has configured in Windows. After that come the shipped .wav files, with a
/// short curated list promoted to the top — of the ~68 files in Windows\Media most
/// are startup chimes and device-insert blips that make poor alert tones.
///
/// Playback is deliberately fire-and-forget: SoundPlayer.Play spawns its own thread,
/// so a long .wav never blocks the UI or delays log ingestion.
/// </summary>
public static class SoundLibrary
{
    public const string DefaultSound = "Windows Notify.wav";
    public const string DefaultFatalSound = "Windows Critical Stop.wav";

    private static readonly Dictionary<string, SoundPlayer> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>Attention-getting without being shrill, in rough order of usefulness.</summary>
    private static readonly string[] Recommended =
    [
        "Windows Notify.wav",
        "Windows Notify System Generic.wav",
        "Windows Message Nudge.wav",
        "Windows Exclamation.wav",
        "Windows Critical Stop.wav",
        "Windows Error.wav",
        "Windows Battery Critical.wav",
        "chimes.wav",
        "chord.wav",
        "ding.wav",
        "notify.wav",
        "tada.wav",
        "Alarm01.wav",
        "Alarm02.wav",
        "Alarm03.wav",
        "Ring01.wav",
        "Ring05.wav",
    ];

    private static IReadOnlyList<AlertSound>? _all;

    public static IReadOnlyList<AlertSound> All => _all ??= Build();

    public static string MediaFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    private static List<AlertSound> Build()
    {
        var list = new List<AlertSound>
        {
            new("system:Beep",        "System: Default beep",   "System"),
            new("system:Asterisk",    "System: Asterisk",       "System"),
            new("system:Exclamation", "System: Exclamation",    "System"),
            new("system:Hand",        "System: Critical stop",  "System"),
            new("system:Question",    "System: Question",       "System"),
        };

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(MediaFolder))
                foreach (var f in Directory.EnumerateFiles(MediaFolder, "*.wav"))
                    present.Add(Path.GetFileName(f));
        }
        catch { /* an unreadable Media folder just means fewer choices */ }

        foreach (var name in Recommended)
            if (present.Contains(name))
                list.Add(new AlertSound(name, Path.GetFileNameWithoutExtension(name), "Recommended"));

        foreach (var name in present.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            if (!Recommended.Contains(name, StringComparer.OrdinalIgnoreCase))
                list.Add(new AlertSound(name, Path.GetFileNameWithoutExtension(name), "All Windows sounds"));

        return list;
    }

    /// <summary>Human-readable name for an id, including one that isn't in the list.</summary>
    public static string Describe(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "(none)";

        var known = All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return known.Display;

        try { return Path.GetFileNameWithoutExtension(id); }
        catch { return id; }
    }

    /// <summary>Adds a user-picked .wav so the dropdown can show and select it.</summary>
    public static AlertSound ForCustomFile(string path)
        => new(path, Path.GetFileNameWithoutExtension(path) + "  (custom)", "Custom");

    public static string? ResolvePath(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith("system:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            if (Path.IsPathRooted(id)) return File.Exists(id) ? id : null;

            var media = Path.Combine(MediaFolder, id);
            return File.Exists(media) ? media : null;
        }
        catch { return null; }
    }

    /// <summary>Plays a sound by id. Never throws — a missing file falls back to a beep.</summary>
    public static void Play(string? id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id)) { SystemSounds.Beep.Play(); return; }

            if (id.StartsWith("system:", StringComparison.OrdinalIgnoreCase))
            {
                PlaySystem(id["system:".Length..]);
                return;
            }

            var path = ResolvePath(id);
            if (path is null) { SystemSounds.Beep.Play(); return; }

            SoundPlayer player;
            lock (Gate)
            {
                if (!Cache.TryGetValue(path, out player!))
                {
                    player = new SoundPlayer(path);
                    // Load once so the first alert doesn't stutter reading from disk.
                    player.Load();
                    Cache[path] = player;
                }
            }

            player.Play();
        }
        catch
        {
            // A missing codec, a locked device, a malformed wav — none of these are
            // worth surfacing during an alert.
            try { SystemSounds.Beep.Play(); } catch { }
        }
    }

    private static void PlaySystem(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "asterisk": SystemSounds.Asterisk.Play(); break;
            case "exclamation": SystemSounds.Exclamation.Play(); break;
            case "hand": SystemSounds.Hand.Play(); break;
            case "question": SystemSounds.Question.Play(); break;
            default: SystemSounds.Beep.Play(); break;
        }
    }
}
