using System.IO;
using System.Text;

namespace LogLens.Services;

public sealed record TailBatch(IReadOnlyList<string> Lines, bool Rewound, string ResolvedPath);

/// <summary>
/// Polls one file and raises new lines as they are appended.
///
/// Deliberate design choices:
///  - Opens with FileShare.ReadWrite | Delete so we never block the writing process.
///    Without Delete, a rotating logger fails to rename its own file while we watch it.
///  - Polls instead of using FileSystemWatcher: watcher events are unreliable on SMB
///    shares and on files written with buffered handles, which is exactly where prod
///    logs live. A length check every 250 ms is cheap and never misses.
///  - Keeps a stateful Decoder so a multi-byte character split across two reads
///    doesn't turn into garbage.
///  - Holds back a trailing partial line until its newline arrives, so you never see
///    a half-written line flicker and then change.
/// </summary>
public sealed class LogTailer : IDisposable
{
    private readonly string _pathSpec;
    private readonly int _initialTailBytes;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;

    private string? _resolvedPath;
    private long _position;
    private Decoder? _decoder;
    private Encoding _encoding = new UTF8Encoding(false);
    private readonly StringBuilder _partial = new();
    private bool _pendingCr;
    private bool _primed;
    private string? _lastError;

    public event Action<TailBatch>? Batch;
    public event Action<string?>? StatusChanged;

    public string PathSpec => _pathSpec;
    public string? ResolvedPath => _resolvedPath;
    public string? LastError => _lastError;

    public LogTailer(string pathSpec, int initialTailBytes)
    {
        _pathSpec = pathSpec;
        _initialTailBytes = initialTailBytes;
    }

    public void Start(int pollMs)
    {
        _timer = new Timer(_ => _ = PollAsync(), null, 0, pollMs);
    }

    public void SetInterval(int pollMs) => _timer?.Change(0, pollMs);

    /// <summary>Re-read the file from the beginning (or from the initial tail window).</summary>
    public async Task ReloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _primed = false;
            _position = 0;
            _decoder = null;
            _partial.Clear();
            _pendingCr = false;
            _resolvedPath = null;
        }
        finally { _gate.Release(); }

        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (_cts.IsCancellationRequested) return;
        if (!await _gate.WaitAsync(0)) return;   // a slow read is still running; skip this tick

        try
        {
            var path = PathResolver.Resolve(_pathSpec);
            if (path is null)
            {
                SetError($"No file matches {_pathSpec}");
                return;
            }

            bool rewound = false;

            // Wildcard rolled onto a different file, or the file was recreated.
            if (!string.Equals(path, _resolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                _resolvedPath = path;
                _position = 0;
                _decoder = null;
                _partial.Clear();
                _pendingCr = false;
                _primed = false;
                rewound = true;
            }

            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1 << 16, useAsync: true);

            long length = fs.Length;

            // Truncated in place (log rotated by overwrite, or someone cleared it).
            // _primed must be cleared too: the decoder is discarded here, and only the
            // priming block below recreates it. Leaving _primed set skipped that block
            // and the next read dereferenced a null decoder.
            if (length < _position)
            {
                _position = 0;
                _decoder = null;
                _partial.Clear();
                _pendingCr = false;
                _primed = false;
                rewound = true;
            }

            bool skipFirstPartialLine = false;

            if (!_primed)
            {
                _primed = true;
                _encoding = await DetectEncodingAsync(fs);
                _decoder = _encoding.GetDecoder();

                if (_initialTailBytes > 0 && length > _initialTailBytes)
                {
                    _position = length - _initialTailBytes;
                    // We almost certainly landed mid-line; drop that fragment.
                    skipFirstPartialLine = true;
                }
                else
                {
                    _position = PreambleLength(_encoding, fs);
                }
                rewound = true;
            }

            if (length == _position)
            {
                SetError(null);
                if (rewound) Batch?.Invoke(new TailBatch(Array.Empty<string>(), true, path));
                return;
            }

            fs.Seek(_position, SeekOrigin.Begin);

            var lines = new List<string>();
            var buffer = new byte[1 << 16];
            var chars = new char[_encoding.GetMaxCharCount(buffer.Length)];
            long remaining = length - _position;

            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = await fs.ReadAsync(buffer.AsMemory(0, want), _cts.Token);
                if (read <= 0) break;
                remaining -= read;

                int charCount = _decoder!.GetChars(buffer, 0, read, chars, 0);
                Consume(chars.AsSpan(0, charCount), lines);

                // A single enormous append shouldn't freeze the UI thread later.
                if (lines.Count > 500_000) break;
            }

            _position = fs.Position;

            if (skipFirstPartialLine && lines.Count > 0) lines.RemoveAt(0);

            SetError(null);

            if (lines.Count > 0 || rewound)
                Batch?.Invoke(new TailBatch(lines, rewound, path));
        }
        catch (OperationCanceledException) { }
        catch (FileNotFoundException) { SetError("File not found"); }
        catch (DirectoryNotFoundException) { SetError("Folder not found"); }
        catch (IOException ex) { SetError(ex.Message); }
        catch (UnauthorizedAccessException) { SetError("Access denied"); }
        catch (Exception ex) { SetError(ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>Split on \n, tolerate \r\n and bare \r, buffer the tail fragment.</summary>
    private void Consume(ReadOnlySpan<char> span, List<string> into)
    {
        int start = 0;

        // A \r\n pair can straddle two reads. Without this the trailing \n opens the
        // next chunk and is emitted as an empty line, which happens on any CRLF file
        // larger than the 64 KB read buffer.
        if (_pendingCr)
        {
            _pendingCr = false;
            if (span.Length > 0 && span[0] == '\n') start = 1;
        }

        for (int i = start; i < span.Length; i++)
        {
            char c = span[i];
            if (c != '\n' && c != '\r') continue;

            _partial.Append(span[start..i]);
            into.Add(_partial.ToString());
            _partial.Clear();

            // Treat \r\n as one break, remembering a \r that ends the chunk.
            if (c == '\r')
            {
                if (i + 1 < span.Length)
                {
                    if (span[i + 1] == '\n') i++;
                }
                else
                {
                    _pendingCr = true;
                }
            }

            start = i + 1;
        }

        if (start < span.Length) _partial.Append(span[start..]);
    }

    private static async Task<Encoding> DetectEncodingAsync(FileStream fs)
    {
        long saved = fs.Position;
        try
        {
            fs.Seek(0, SeekOrigin.Begin);
            var bom = new byte[4];
            int n = await fs.ReadAsync(bom.AsMemory(0, 4));

            if (n >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(true);
            if (n >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0 && bom[3] == 0) return new UTF32Encoding(false, true);
            if (n >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF) return new UTF32Encoding(true, true);
            if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return new UnicodeEncoding(false, true);
            if (n >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return new UnicodeEncoding(true, true);

            // No BOM: UTF-8 is the safe default and degrades gracefully on ASCII.
            return new UTF8Encoding(false);
        }
        finally { fs.Seek(saved, SeekOrigin.Begin); }
    }

    private static long PreambleLength(Encoding enc, FileStream fs)
    {
        var pre = enc.GetPreamble();
        if (pre.Length == 0 || fs.Length < pre.Length) return 0;

        long saved = fs.Position;
        try
        {
            fs.Seek(0, SeekOrigin.Begin);
            var head = new byte[pre.Length];
            if (fs.Read(head, 0, head.Length) != head.Length) return 0;
            return head.SequenceEqual(pre) ? pre.Length : 0;
        }
        finally { fs.Seek(saved, SeekOrigin.Begin); }
    }

    private void SetError(string? message)
    {
        if (_lastError == message) return;
        _lastError = message;
        StatusChanged?.Invoke(message);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _timer = null;
    }
}
