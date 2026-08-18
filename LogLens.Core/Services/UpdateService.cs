using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LogLens.Services;

public sealed record UpdateInfo(
    Version Current,
    Version Latest,
    string TagName,
    string ReleaseUrl,
    string ExeDownloadUrl,
    long ExeSizeBytes,
    string? ChecksumDownloadUrl);

/// <summary>
/// Checks GitHub releases for a newer LogLens and swaps the running exe in place.
///
/// The swap uses a Windows quirk that avoids helper scripts entirely: a running
/// executable's file cannot be deleted or overwritten, but it CAN be renamed. So:
/// rename LogLens.exe to LogLens.exe.old, move the verified download into place as
/// LogLens.exe, start it, and exit. The next start deletes the leftover .old.
///
/// The download is verified against the SHA256SUMS.txt published with every release
/// before anything is touched — an update mechanism is the worst possible place to
/// skip integrity checks.
/// </summary>
public static class UpdateService
{
    private const string Owner = "JollyKrampus";
    private const string Repo = "loglens";
    private const string ExeAssetName = "LogLens.exe";
    private const string ChecksumAssetName = "SHA256SUMS.txt";

    public const string StagedSuffix = ".update";
    public const string BackupSuffix = ".old";

    // Lazily created: an eager field initializer here ran BEFORE CurrentVersion's
    // (textual order), so building the User-Agent dereferenced a null version and
    // every call died in the type initializer. Lazy defers until first use, when
    // all statics are ready — regardless of declaration order.
    private static readonly Lazy<HttpClient> HttpLazy = new(CreateClient);
    private static HttpClient Http => HttpLazy.Value;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // The GitHub API rejects requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LogLens", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    private static Version ReadCurrentVersion()
    {
        // The ENTRY assembly, not the executing one: this code now lives in
        // LogLens.Core, whose own version is irrelevant — the exe's version is what
        // updates compare against. GetEntryAssembly is null only in exotic hosts.
        var exe = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var informational = exe
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return ParseVersion(informational)
               ?? exe.GetName().Version
               ?? new Version(0, 0, 0);
    }

    /// <summary>"v1.2.0" or "1.2.0+abc123" to a comparable Version; null when unparseable.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        // "1.2" parses but compares oddly against "1.2.0"; normalise to three parts.
        if (Version.TryParse(s, out var v))
            return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        return null;
    }

    /// <summary>Pulls the expected hash for a file out of a SHA256SUMS.txt body.</summary>
    public static string? ParseChecksum(string? sumsContent, string fileName)
    {
        if (string.IsNullOrWhiteSpace(sumsContent)) return null;

        foreach (var raw in sumsContent.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[^1], fileName, StringComparison.OrdinalIgnoreCase)
                && parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }

        return null;
    }

    /// <summary>
    /// Asks GitHub for the latest release. Returns null when already up to date.
    /// <paramref name="currentOverride"/> exists for tests; production passes null.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default, Version? currentOverride = null)
    {
        var current = currentOverride ?? CurrentVersion;

        using var response = await Http.GetAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var latest = ParseVersion(tag);
        if (latest is null || latest <= current) return null;

        string? exeUrl = null, sumsUrl = null;
        long exeSize = 0;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, ExeAssetName, StringComparison.OrdinalIgnoreCase))
            {
                exeUrl = asset.GetProperty("browser_download_url").GetString();
                exeSize = asset.GetProperty("size").GetInt64();
            }
            else if (string.Equals(name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
            {
                sumsUrl = asset.GetProperty("browser_download_url").GetString();
            }
        }

        if (exeUrl is null) return null;   // a release with no exe is not an update

        var releaseUrl = root.GetProperty("html_url").GetString()
                         ?? $"https://github.com/{Owner}/{Repo}/releases/tag/{tag}";

        return new UpdateInfo(current, latest, tag, releaseUrl, exeUrl, exeSize, sumsUrl);
    }

    /// <summary>
    /// Downloads the new exe next to the current one and verifies its checksum.
    /// Returns the staged file's path, or throws with a user-readable message.
    /// </summary>
    public static async Task<string> DownloadAndVerifyAsync(
        UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine where LogLens is running from.");
        var staged = exePath + StagedSuffix;

        try
        {
            await using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var response = await Http.GetAsync(update.ExeDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? update.ExeSizeBytes;
                await using var input = await response.Content.ReadAsStreamAsync(ct);

                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            if (update.ChecksumDownloadUrl is not null)
            {
                var sums = await Http.GetStringAsync(update.ChecksumDownloadUrl, ct);
                var expected = ParseChecksum(sums, ExeAssetName);

                if (expected is not null)
                {
                    await using var file = File.OpenRead(staged);
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();

                    if (actual != expected)
                        throw new InvalidOperationException(
                            "The downloaded file's checksum does not match the published one. "
                            + "Not installing it. Try again, or download from the releases page directly.");
                }
            }

            return staged;
        }
        catch
        {
            try { File.Delete(staged); } catch { }
            throw;
        }
    }

    /// <summary>
    /// The rename dance, on explicit paths so it can be tested with plain files:
    /// current exe becomes .old, the staged file becomes the exe.
    /// </summary>
    public static void PerformSwap(string exePath, string stagedPath)
    {
        var backup = exePath + BackupSuffix;

        // A leftover .old can be held open by a virus scanner or a backup tool, and
        // startup cleanup gives up quietly on it. Renaming the running exe to a FRESH
        // name always works, so step aside rather than fail the whole update over a
        // file that only exists to be deleted. Cleanup collects the variants later.
        try
        {
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch
        {
            backup = exePath + BackupSuffix + "-" + Environment.TickCount64;
        }

        File.Move(exePath, backup);

        try
        {
            File.Move(stagedPath, exePath);
        }
        catch
        {
            // Put the world back rather than leave no exe at all.
            File.Move(backup, exePath);
            throw;
        }
    }

    /// <summary>The argument the new instance uses to wait for its predecessor.</summary>
    public const string UpdatedFromArg = "--updated-from";

    /// <summary>
    /// Swaps the running exe for the staged download and starts the new one. The new
    /// instance is told our PID so it can WAIT for us to fully exit before touching
    /// anything — the old instance still has the workspace file and the issue
    /// database open while it shuts down, and starting the successor into that
    /// overlap is what made the outgoing version die with an error dialog.
    /// </summary>
    public static void ApplyAndRestart(string stagedPath)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine where LogLens is running from.");

        PerformSwap(exePath, stagedPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Arguments = $"{UpdatedFromArg} {Environment.ProcessId}"
        });
    }

    /// <summary>
    /// If launched by a self-update, block until the predecessor has exited so its
    /// shutdown (workspace save, database checkpoint) finishes before we begin.
    /// Bounded: a hung predecessor delays us at most <paramref name="maxWaitMs"/>.
    /// </summary>
    public static void WaitForPredecessor(string[] args, int maxWaitMs = 15_000)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != UpdatedFromArg || !int.TryParse(args[i + 1], out var pid)) continue;

            try
            {
                using var predecessor = System.Diagnostics.Process.GetProcessById(pid);
                predecessor.WaitForExit(maxWaitMs);
            }
            catch { /* already gone — exactly what we want */ }
            return;
        }
    }

    /// <summary>
    /// Removes files a previous update left behind. Retries briefly: when the update
    /// came from a version that did not pass <see cref="UpdatedFromArg"/>, the old
    /// instance may still be exiting and holding its (renamed) image file — the
    /// retry doubles as a wait for it.
    /// </summary>
    public static void CleanUpLeftovers()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        // .old plus any .old-<ticks> variants PerformSwap had to step around.
        var leftovers = new List<string> { exePath + StagedSuffix };
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (dir is not null)
                leftovers.AddRange(Directory.GetFiles(dir, Path.GetFileName(exePath) + BackupSuffix + "*"));
        }
        catch
        {
            leftovers.Add(exePath + BackupSuffix);
        }

        foreach (var leftover in leftovers)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (!File.Exists(leftover)) break;
                    File.Delete(leftover);
                    break;
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
        }
    }
}
