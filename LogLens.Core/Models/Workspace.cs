using System.Text.Json.Serialization;
using LogLens.Core;

namespace LogLens.Models;

/// <summary>A file (or wildcard) being tailed inside a view.</summary>
public sealed class LogSource : ObservableObject
{
    private string _name = "";
    private string _path = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name for the tab. Falls back to the file name.</summary>
    public string Name
    {
        get => string.IsNullOrWhiteSpace(_name) ? DefaultName : _name;
        set => Set(ref _name, value);
    }

    /// <summary>Absolute path. May contain * or ? — the newest match is tailed.</summary>
    public string Path { get => _path; set { Set(ref _path, value); Raise(nameof(Name)); } }

    [JsonIgnore]
    public string DefaultName
    {
        get
        {
            try { return System.IO.Path.GetFileName(_path) is { Length: > 0 } f ? f : "(no file)"; }
            catch { return "(no file)"; }
        }
    }

    public LogSource Clone() => new() { Name = _name, Path = _path };
}

/// <summary>Live include/exclude filtering. Applied on top of the highlight rules.</summary>
public sealed class FilterSpec : ObservableObject
{
    private string _include = "";
    private string _exclude = "";
    private bool _isRegex;
    private bool _caseSensitive;

    public string Include { get => _include; set => Set(ref _include, value); }
    public string Exclude { get => _exclude; set => Set(ref _exclude, value); }
    public bool IsRegex { get => _isRegex; set => Set(ref _isRegex, value); }
    public bool CaseSensitive { get => _caseSensitive; set => Set(ref _caseSensitive, value); }

    [JsonIgnore]
    public bool IsActive => !string.IsNullOrEmpty(Include) || !string.IsNullOrEmpty(Exclude);

    public FilterSpec Clone() => new()
    {
        Include = Include, Exclude = Exclude, IsRegex = IsRegex, CaseSensitive = CaseSensitive
    };
}

/// <summary>A named group of log files — "Dev", "Test", "Prod".</summary>
public sealed class ViewDef : ObservableObject
{
    private string _name = "New view";
    private string _accent = "#4C8DFF";
    private bool _showMergedTimeline;
    private bool _alertsEnabled = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Adds a "Merged" tab interleaving every file in this view by timestamp.</summary>
    public bool ShowMergedTimeline { get => _showMergedTimeline; set => Set(ref _showMergedTimeline, value); }

    /// <summary>Lets you mute dev while still being alerted about prod.</summary>
    public bool AlertsEnabled { get => _alertsEnabled; set => Set(ref _alertsEnabled, value); }

    /// <summary>Colour stripe in the sidebar, so prod is visually unmistakable.</summary>
    public string Accent { get => _accent; set => Set(ref _accent, value); }

    public List<LogSource> Sources { get; set; } = [];

    /// <summary>Rules that apply on top of the workspace-wide rules, evaluated first.</summary>
    public List<HighlightRule> Rules { get; set; } = [];

    public ViewDef Clone() => new()
    {
        Name = Name + " (copy)",
        Accent = Accent,
        ShowMergedTimeline = ShowMergedTimeline,
        AlertsEnabled = AlertsEnabled,
        Sources = Sources.Select(s => s.Clone()).ToList(),
        Rules = Rules.Select(r => r.Clone()).ToList()
    };
}

/// <summary>What to do when errors show up while you're looking elsewhere.</summary>
public sealed class AlertSettings : ObservableObject
{
    private bool _enabled = true;
    private Severity _minimumSeverity = Severity.Error;
    private string _customPattern = "";
    private bool _showToast = true;
    private bool _playSound = true;
    // Literals rather than SoundLibrary constants: SoundLibrary is Windows-only and
    // lives in the app, while this model is cross-platform. The app's SoundLibrary
    // declares the same values as its defaults.
    private string _soundName = "Windows Notify.wav";
    private string _fatalSoundName = "Windows Critical Stop.wav";
    private bool _useDistinctFatalSound = true;
    private bool _flashTaskbar = true;
    private bool _onlyWhenUnfocused = true;
    private int _throttleSeconds = 15;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Alert on this severity and anything above it.</summary>
    public Severity MinimumSeverity { get => _minimumSeverity; set => Set(ref _minimumSeverity, value); }

    /// <summary>Optional regex that alerts regardless of severity — a specific error code, say.</summary>
    public string CustomPattern { get => _customPattern; set => Set(ref _customPattern, value); }

    public bool ShowToast { get => _showToast; set => Set(ref _showToast, value); }
    public bool PlaySound { get => _playSound; set => Set(ref _playSound, value); }

    /// <summary>
    /// Sound id: "system:Name", a file name under %WinDir%\Media, or an absolute
    /// path to any .wav the user picked.
    /// </summary>
    public string SoundName { get => _soundName; set => Set(ref _soundName, value); }

    /// <summary>A fatal deserves to sound different from a run-of-the-mill error.</summary>
    public string FatalSoundName { get => _fatalSoundName; set => Set(ref _fatalSoundName, value); }

    public bool UseDistinctFatalSound
    {
        get => _useDistinctFatalSound;
        set => Set(ref _useDistinctFatalSound, value);
    }

    /// <summary>The sound actually used for a given severity.</summary>
    public string SoundFor(Severity severity)
        => severity == Severity.Fatal && UseDistinctFatalSound ? FatalSoundName : SoundName;
    public bool FlashTaskbar { get => _flashTaskbar; set => Set(ref _flashTaskbar, value); }

    /// <summary>No point shouting at you about a line you are already looking at.</summary>
    public bool OnlyWhenUnfocused { get => _onlyWhenUnfocused; set => Set(ref _onlyWhenUnfocused, value); }

    /// <summary>At most one alert per view per this many seconds — a log storm must not spam.</summary>
    public int ThrottleSeconds
    {
        get => _throttleSeconds;
        set => Set(ref _throttleSeconds, Math.Clamp(value, 0, 3600));
    }
}

public sealed class AppSettings : ObservableObject
{
    private int _pollIntervalMs = 250;
    private int _maxLines = 200_000;
    private int _initialTailKb = 2048;
    private string _fontFamily = "Cascadia Mono, Consolas, Courier New";
    private double _fontSize = 12.5;
    private bool _showLineNumbers = true;
    private bool _wordWrap;
    private bool _lightTheme;
    private int _mergeWindowMs = 1000;
    private bool _trackIssues = true;
    private string _jiraBaseUrl = "";
    private string _jiraProjectKey = "";

    /// <summary>How often each file is checked for growth. 250 ms feels instant.</summary>
    public int PollIntervalMs { get => _pollIntervalMs; set => Set(ref _pollIntervalMs, Math.Clamp(value, 50, 10_000)); }

    /// <summary>Ring-buffer cap per tab. Oldest lines drop off the top.</summary>
    public int MaxLines { get => _maxLines; set => Set(ref _maxLines, Math.Clamp(value, 1_000, 5_000_000)); }

    /// <summary>On open, read only the last N KB. 0 loads the whole file.</summary>
    public int InitialTailKb { get => _initialTailKb; set => Set(ref _initialTailKb, Math.Max(0, value)); }

    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 6, 42)); }
    public bool ShowLineNumbers { get => _showLineNumbers; set => Set(ref _showLineNumbers, value); }
    public bool WordWrap { get => _wordWrap; set => Set(ref _wordWrap, value); }
    public bool LightTheme { get => _lightTheme; set => Set(ref _lightTheme, value); }

    /// <summary>
    /// How long the merged timeline holds a line before releasing it, so slower
    /// files get a chance to deliver their older lines first. Raise it if your logs
    /// live on a laggy share and the merged view reports late batches.
    /// </summary>
    public int MergeWindowMs
    {
        get => _mergeWindowMs;
        set => Set(ref _mergeWindowMs, Math.Clamp(value, 0, 30_000));
    }

    /// <summary>
    /// Accumulate distinct fatal/error/warn problems into the local issue database.
    /// Off means nothing is written and no database file is created.
    /// </summary>
    public bool TrackIssues { get => _trackIssues; set => Set(ref _trackIssues, value); }

    /// <summary>e.g. https://yourcompany.atlassian.net — enables the "open in Jira" links.</summary>
    public string JiraBaseUrl { get => _jiraBaseUrl; set => Set(ref _jiraBaseUrl, value); }

    /// <summary>Default project key put on generated tickets, e.g. PLAT.</summary>
    public string JiraProjectKey { get => _jiraProjectKey; set => Set(ref _jiraProjectKey, value); }

    private bool _checkForUpdates = true;

    /// <summary>
    /// One quiet GitHub releases lookup shortly after startup. Finding something
    /// only produces a status-bar note — never a download without you asking.
    /// </summary>
    public bool CheckForUpdates { get => _checkForUpdates; set => Set(ref _checkForUpdates, value); }
}

/// <summary>Everything persisted to loglens.workspace.json.</summary>
public sealed class Workspace
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public List<HighlightRule> Rules { get; set; } = HighlightRule.Defaults();
    public List<ViewDef> Views { get; set; } = [];
    public string? ActiveViewId { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    public static Workspace CreateDefault()
    {
        var ws = new Workspace();
        ws.Views.Add(new ViewDef { Name = "Dev",  Accent = "#4CAF50" });
        ws.Views.Add(new ViewDef { Name = "Test", Accent = "#FFB300" });
        ws.Views.Add(new ViewDef { Name = "Prod", Accent = "#EF5350" });
        ws.ActiveViewId = ws.Views[0].Id;
        return ws;
    }
}
