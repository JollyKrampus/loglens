using System.IO;
using Microsoft.Data.Sqlite;
using LogLens.Models;

namespace LogLens.Services;

/// <summary>One distinct problem, aggregated across every time it has been seen.</summary>
public sealed class LogIssue
{
    public string Hash { get; set; } = "";
    public Severity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Signature { get; set; } = "";
    public string? ExceptionType { get; set; }
    public string? FaultingMethod { get; set; }
    public string? Logger { get; set; }

    /// <summary>A real, unmasked line, so the ticket can show what it actually looked like.</summary>
    public string SampleLine { get; set; } = "";

    /// <summary>Stack trace / inner exception lines that followed the sample.</summary>
    public string? SampleDetail { get; set; }

    public long Count { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Comma-separated view and file names this has been seen in.</summary>
    public string Views { get; set; } = "";
    public string Sources { get; set; } = "";

    /// <summary>Set once you've raised the ticket, so it stops looking new.</summary>
    public string? JiraKey { get; set; }
    public string? Notes { get; set; }
    public bool Ignored { get; set; }

    public DateTime FirstSeenLocal => DateTime.SpecifyKind(FirstSeenUtc, DateTimeKind.Utc).ToLocalTime();
    public DateTime LastSeenLocal => DateTime.SpecifyKind(LastSeenUtc, DateTimeKind.Utc).ToLocalTime();
    public bool IsFiled => !string.IsNullOrWhiteSpace(JiraKey);
}

/// <summary>A single sighting, queued for the background writer.</summary>
public sealed record IssueOccurrence(
    IssueFingerprint Fingerprint,
    Severity Severity,
    string Line,
    string? Detail,
    string View,
    string Source,
    DateTime WhenUtc);

/// <summary>
/// The local aggregating database.
///
/// SQLite rather than a JSON file because the whole point is accumulating counts
/// over weeks: an upsert with a counter increment is one statement, and it stays
/// fast at millions of sightings. Writes are batched off the UI thread — log
/// ingestion must never wait on a disk write.
///
/// The file sits beside the workspace, so a portable install keeps its history on
/// the stick with it.
/// </summary>
public sealed class IssueStore : IDisposable
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public string DatabasePath { get; }

    public IssueStore(string databasePath)
    {
        DatabasePath = databasePath;

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialise();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Initialise()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;

                CREATE TABLE IF NOT EXISTS issues (
                    hash            TEXT PRIMARY KEY,
                    severity        INTEGER NOT NULL,
                    title           TEXT NOT NULL,
                    signature       TEXT NOT NULL,
                    exception_type  TEXT,
                    faulting_method TEXT,
                    logger          TEXT,
                    sample_line     TEXT NOT NULL,
                    sample_detail   TEXT,
                    count           INTEGER NOT NULL DEFAULT 0,
                    first_seen_utc  TEXT NOT NULL,
                    last_seen_utc   TEXT NOT NULL,
                    views           TEXT NOT NULL DEFAULT '',
                    sources         TEXT NOT NULL DEFAULT '',
                    jira_key        TEXT,
                    notes           TEXT,
                    ignored         INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS ix_issues_severity ON issues(severity, last_seen_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_issues_count    ON issues(count DESC);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Upserts a batch of sightings in one transaction.</summary>
    public void Record(IReadOnlyList<IssueOccurrence> batch)
    {
        if (batch.Count == 0) return;

        lock (_gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();

            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO issues
                    (hash, severity, title, signature, exception_type, faulting_method, logger,
                     sample_line, sample_detail, count, first_seen_utc, last_seen_utc, views, sources)
                VALUES
                    ($hash, $severity, $title, $signature, $exType, $method, $logger,
                     $sample, $detail, 1, $when, $when, $view, $source)
                ON CONFLICT(hash) DO UPDATE SET
                    count         = count + 1,
                    last_seen_utc = excluded.last_seen_utc,
                    -- Keep the richest sample: one that carries a stack trace beats one that doesn't.
                    sample_line   = CASE WHEN issues.sample_detail IS NULL AND excluded.sample_detail IS NOT NULL
                                         THEN excluded.sample_line ELSE issues.sample_line END,
                    sample_detail = COALESCE(issues.sample_detail, excluded.sample_detail),
                    views         = CASE WHEN instr(',' || issues.views   || ',', ',' || excluded.views   || ',') > 0
                                         THEN issues.views   ELSE issues.views   || ',' || excluded.views   END,
                    sources       = CASE WHEN instr(',' || issues.sources || ',', ',' || excluded.sources || ',') > 0
                                         THEN issues.sources ELSE issues.sources || ',' || excluded.sources END;
                """;

            var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
            var pSev = cmd.Parameters.Add("$severity", SqliteType.Integer);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pSig = cmd.Parameters.Add("$signature", SqliteType.Text);
            var pType = cmd.Parameters.Add("$exType", SqliteType.Text);
            var pMethod = cmd.Parameters.Add("$method", SqliteType.Text);
            var pLogger = cmd.Parameters.Add("$logger", SqliteType.Text);
            var pSample = cmd.Parameters.Add("$sample", SqliteType.Text);
            var pDetail = cmd.Parameters.Add("$detail", SqliteType.Text);
            var pWhen = cmd.Parameters.Add("$when", SqliteType.Text);
            var pView = cmd.Parameters.Add("$view", SqliteType.Text);
            var pSource = cmd.Parameters.Add("$source", SqliteType.Text);

            foreach (var o in batch)
            {
                pHash.Value = o.Fingerprint.Hash;
                pSev.Value = (int)o.Severity;
                pTitle.Value = o.Fingerprint.Title;
                pSig.Value = o.Fingerprint.Signature;
                pType.Value = (object?)o.Fingerprint.ExceptionType ?? DBNull.Value;
                pMethod.Value = (object?)o.Fingerprint.FaultingMethod ?? DBNull.Value;
                pLogger.Value = (object?)o.Fingerprint.Logger ?? DBNull.Value;
                pSample.Value = o.Line;
                pDetail.Value = (object?)o.Detail ?? DBNull.Value;
                pWhen.Value = o.WhenUtc.ToString("O");
                pView.Value = Clean(o.View);
                pSource.Value = Clean(o.Source);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Commas are the list separator, so they cannot appear inside a value.</summary>
    private static string Clean(string s) => (s ?? "").Replace(',', ' ').Trim();

    public List<LogIssue> Query(Severity? severity = null, bool includeIgnored = false,
                                bool includeFiled = true, string? search = null, int limit = 2000)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();

            var where = new List<string>();
            if (severity is not null) where.Add("severity = $sev");
            if (!includeIgnored) where.Add("ignored = 0");
            if (!includeFiled) where.Add("(jira_key IS NULL OR jira_key = '')");
            if (!string.IsNullOrWhiteSpace(search)) where.Add("(title LIKE $q OR signature LIKE $q OR sample_line LIKE $q)");

            cmd.CommandText =
                "SELECT hash, severity, title, signature, exception_type, faulting_method, logger, " +
                "       sample_line, sample_detail, count, first_seen_utc, last_seen_utc, views, sources, " +
                "       jira_key, notes, ignored " +
                "FROM issues " +
                (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "") +
                "ORDER BY severity DESC, count DESC, last_seen_utc DESC " +
                "LIMIT $limit";

            if (severity is not null) cmd.Parameters.AddWithValue("$sev", (int)severity);
            if (!string.IsNullOrWhiteSpace(search)) cmd.Parameters.AddWithValue("$q", "%" + search.Trim() + "%");
            cmd.Parameters.AddWithValue("$limit", limit);

            var list = new List<LogIssue>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadIssue(r));
            return list;
        }
    }

    private static LogIssue ReadIssue(SqliteDataReader r) => new()
    {
        Hash = r.GetString(0),
        Severity = (Severity)r.GetInt32(1),
        Title = r.GetString(2),
        Signature = r.GetString(3),
        ExceptionType = r.IsDBNull(4) ? null : r.GetString(4),
        FaultingMethod = r.IsDBNull(5) ? null : r.GetString(5),
        Logger = r.IsDBNull(6) ? null : r.GetString(6),
        SampleLine = r.GetString(7),
        SampleDetail = r.IsDBNull(8) ? null : r.GetString(8),
        Count = r.GetInt64(9),
        FirstSeenUtc = DateTime.Parse(r.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
        LastSeenUtc = DateTime.Parse(r.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
        Views = r.GetString(12),
        Sources = r.GetString(13),
        JiraKey = r.IsDBNull(14) ? null : r.GetString(14),
        Notes = r.IsDBNull(15) ? null : r.GetString(15),
        Ignored = r.GetInt32(16) != 0,
    };

    public void SetJiraKey(string hash, string? key) => Update(hash, "jira_key", (object?)key ?? DBNull.Value);
    public void SetNotes(string hash, string? notes) => Update(hash, "notes", (object?)notes ?? DBNull.Value);
    public void SetIgnored(string hash, bool ignored) => Update(hash, "ignored", ignored ? 1 : 0);

    private void Update(string hash, string column, object value)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE issues SET {column} = $v WHERE hash = $h";
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(string hash)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM issues WHERE hash = $h";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.ExecuteNonQuery();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM issues";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Distinct-issue counts per severity, for the window's header.</summary>
    public Dictionary<Severity, int> CountsBySeverity(bool includeIgnored = false)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT severity, COUNT(*) FROM issues " +
                              (includeIgnored ? "" : "WHERE ignored = 0 ") +
                              "GROUP BY severity";

            var result = new Dictionary<Severity, int>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[(Severity)r.GetInt32(0)] = r.GetInt32(1);
            return result;
        }
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}
