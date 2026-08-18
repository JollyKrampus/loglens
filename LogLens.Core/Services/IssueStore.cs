using System.IO;
using Microsoft.Data.Sqlite;
using LogLens.Models;

namespace LogLens.Services;

/// <summary>One distinct problem, aggregated per view it was seen in.</summary>
public sealed class LogIssue
{
    public string Hash { get; set; } = "";

    /// <summary>
    /// The view (Dev, Test, Prod…) this row belongs to. Issues are deliberately NOT
    /// merged across views: a bug already fixed in dev can still be live in prod,
    /// and dev noise from mid-development must not pollute the prod list — so the
    /// same signature in two views is two rows with independent counts, Jira keys
    /// and ignore flags.
    /// </summary>
    public string View { get; set; } = "";

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

    /// <summary>Comma-separated file names this has been seen in, within its view.</summary>
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
/// The key is (hash, view): the same fault in Dev and Prod is two independent rows.
/// Databases written by 1.3 and earlier keyed on hash alone with an accumulated
/// views list; Initialise migrates them, carrying each old row over with its views
/// string as the view value, so history is kept and new sightings scope correctly.
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

        // A closing predecessor (during a self-update handoff from a version that
        // doesn't signal us) can hold the database for a moment. Retry briefly
        // rather than greeting the user with a crash dialog on first launch.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Initialise();
                break;
            }
            catch (SqliteException) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
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

            using (var pragma = c.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                pragma.ExecuteNonQuery();
            }

            bool hasTable, hasViewColumn = false;
            using (var probe = c.CreateCommand())
            {
                probe.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='issues'";
                hasTable = probe.ExecuteScalar() is not null;
            }

            if (hasTable)
            {
                using var cols = c.CreateCommand();
                cols.CommandText = "PRAGMA table_info(issues)";
                using var r = cols.ExecuteReader();
                while (r.Read())
                    if (r.GetString(1) == "view") hasViewColumn = true;
            }

            if (hasTable && !hasViewColumn)
            {
                MigrateFromV1(c);
                return;
            }

            using var cmd = c.CreateCommand();
            cmd.CommandText = CreateTableSql("issues");
            cmd.ExecuteNonQuery();
        }
    }

    private static string CreateTableSql(string name) => $"""
        CREATE TABLE IF NOT EXISTS {name} (
            hash            TEXT NOT NULL,
            view            TEXT NOT NULL DEFAULT '',
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
            sources         TEXT NOT NULL DEFAULT '',
            jira_key        TEXT,
            notes           TEXT,
            ignored         INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (hash, view)
        );

        CREATE INDEX IF NOT EXISTS ix_issues_view_sev ON {name}(view, severity, last_seen_utc DESC);
        CREATE INDEX IF NOT EXISTS ix_issues_count    ON {name}(count DESC);
        """;

    /// <summary>
    /// v1 (≤1.3) keyed on hash alone and accumulated view names into a 'views'
    /// column. Each old row carries over with that string as its view — a row that
    /// had genuinely mixed views keeps its combined label (e.g. "Prod,Test") and its
    /// history, while everything recorded from now on lands in per-view rows.
    /// </summary>
    private static void MigrateFromV1(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;

        // Drop v1's indexes FIRST. SQLite index names are schema-wide, so with the
        // old ix_issues_count still present, the CREATE INDEX IF NOT EXISTS in the
        // v2 DDL is silently skipped — and the DROP TABLE below then deletes the old
        // index, leaving the migrated database without a count index at all.
        cmd.CommandText = """
            DROP INDEX IF EXISTS ix_issues_severity;
            DROP INDEX IF EXISTS ix_issues_count;

            """ + CreateTableSql("issues_v2") + """

            INSERT INTO issues_v2
                (hash, view, severity, title, signature, exception_type, faulting_method,
                 logger, sample_line, sample_detail, count, first_seen_utc, last_seen_utc,
                 sources, jira_key, notes, ignored)
            SELECT hash, COALESCE(views, ''), severity, title, signature, exception_type,
                   faulting_method, logger, sample_line, sample_detail, count,
                   first_seen_utc, last_seen_utc, COALESCE(sources, ''), jira_key, notes, ignored
            FROM issues;

            DROP TABLE issues;
            ALTER TABLE issues_v2 RENAME TO issues;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
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
                    (hash, view, severity, title, signature, exception_type, faulting_method, logger,
                     sample_line, sample_detail, count, first_seen_utc, last_seen_utc, sources)
                VALUES
                    ($hash, $view, $severity, $title, $signature, $exType, $method, $logger,
                     $sample, $detail, 1, $when, $when, $source)
                ON CONFLICT(hash, view) DO UPDATE SET
                    count         = count + 1,
                    last_seen_utc = excluded.last_seen_utc,
                    -- Keep the richest sample: one that carries a stack trace beats one that doesn't.
                    sample_line   = CASE WHEN issues.sample_detail IS NULL AND excluded.sample_detail IS NOT NULL
                                         THEN excluded.sample_line ELSE issues.sample_line END,
                    sample_detail = COALESCE(issues.sample_detail, excluded.sample_detail),
                    sources       = CASE WHEN instr(',' || issues.sources || ',', ',' || excluded.sources || ',') > 0
                                         THEN issues.sources ELSE issues.sources || ',' || excluded.sources END;
                """;

            var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
            var pView = cmd.Parameters.Add("$view", SqliteType.Text);
            var pSev = cmd.Parameters.Add("$severity", SqliteType.Integer);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pSig = cmd.Parameters.Add("$signature", SqliteType.Text);
            var pType = cmd.Parameters.Add("$exType", SqliteType.Text);
            var pMethod = cmd.Parameters.Add("$method", SqliteType.Text);
            var pLogger = cmd.Parameters.Add("$logger", SqliteType.Text);
            var pSample = cmd.Parameters.Add("$sample", SqliteType.Text);
            var pDetail = cmd.Parameters.Add("$detail", SqliteType.Text);
            var pWhen = cmd.Parameters.Add("$when", SqliteType.Text);
            var pSource = cmd.Parameters.Add("$source", SqliteType.Text);

            foreach (var o in batch)
            {
                pHash.Value = o.Fingerprint.Hash;
                pView.Value = Clean(o.View);
                pSev.Value = (int)o.Severity;
                pTitle.Value = o.Fingerprint.Title;
                pSig.Value = o.Fingerprint.Signature;
                pType.Value = (object?)o.Fingerprint.ExceptionType ?? DBNull.Value;
                pMethod.Value = (object?)o.Fingerprint.FaultingMethod ?? DBNull.Value;
                pLogger.Value = (object?)o.Fingerprint.Logger ?? DBNull.Value;
                pSample.Value = o.Line;
                pDetail.Value = (object?)o.Detail ?? DBNull.Value;
                pWhen.Value = o.WhenUtc.ToString("O");
                pSource.Value = Clean(o.Source);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Commas are the list separator, so they cannot appear inside a value.</summary>
    private static string Clean(string s) => (s ?? "").Replace(',', ' ').Trim();

    /// <summary>Every view name that has recorded at least one issue.</summary>
    public List<string> DistinctViews()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT view FROM issues ORDER BY view";

            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public List<LogIssue> Query(Severity? severity = null, string? view = null,
                                bool includeIgnored = false, bool includeFiled = true,
                                string? search = null, int limit = 2000)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();

            var where = new List<string>();
            if (severity is not null) where.Add("severity = $sev");
            if (view is not null) where.Add("view = $view");
            if (!includeIgnored) where.Add("ignored = 0");
            if (!includeFiled) where.Add("(jira_key IS NULL OR jira_key = '')");
            if (!string.IsNullOrWhiteSpace(search)) where.Add("(title LIKE $q OR signature LIKE $q OR sample_line LIKE $q)");

            cmd.CommandText =
                "SELECT hash, view, severity, title, signature, exception_type, faulting_method, logger, " +
                "       sample_line, sample_detail, count, first_seen_utc, last_seen_utc, sources, " +
                "       jira_key, notes, ignored " +
                "FROM issues " +
                (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "") +
                "ORDER BY severity DESC, count DESC, last_seen_utc DESC " +
                "LIMIT $limit";

            if (severity is not null) cmd.Parameters.AddWithValue("$sev", (int)severity);
            if (view is not null) cmd.Parameters.AddWithValue("$view", view);
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
        View = r.GetString(1),
        Severity = (Severity)r.GetInt32(2),
        Title = r.GetString(3),
        Signature = r.GetString(4),
        ExceptionType = r.IsDBNull(5) ? null : r.GetString(5),
        FaultingMethod = r.IsDBNull(6) ? null : r.GetString(6),
        Logger = r.IsDBNull(7) ? null : r.GetString(7),
        SampleLine = r.GetString(8),
        SampleDetail = r.IsDBNull(9) ? null : r.GetString(9),
        Count = r.GetInt64(10),
        FirstSeenUtc = DateTime.Parse(r.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
        LastSeenUtc = DateTime.Parse(r.GetString(12), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
        Sources = r.GetString(13),
        JiraKey = r.IsDBNull(14) ? null : r.GetString(14),
        Notes = r.IsDBNull(15) ? null : r.GetString(15),
        Ignored = r.GetInt32(16) != 0,
    };

    public void SetJiraKey(string hash, string view, string? key) => Update(hash, view, "jira_key", (object?)key ?? DBNull.Value);
    public void SetNotes(string hash, string view, string? notes) => Update(hash, view, "notes", (object?)notes ?? DBNull.Value);
    public void SetIgnored(string hash, string view, bool ignored) => Update(hash, view, "ignored", ignored ? 1 : 0);

    private void Update(string hash, string view, string column, object value)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE issues SET {column} = $v WHERE hash = $h AND view = $view";
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$view", view);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(string hash, string view)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM issues WHERE hash = $h AND view = $view";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$view", view);
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

    /// <summary>Distinct-issue counts per severity, optionally within one view.</summary>
    public Dictionary<Severity, int> CountsBySeverity(bool includeIgnored = false, string? view = null)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();

            var where = new List<string>();
            if (!includeIgnored) where.Add("ignored = 0");
            if (view is not null) { where.Add("view = $view"); cmd.Parameters.AddWithValue("$view", view); }

            cmd.CommandText = "SELECT severity, COUNT(*) FROM issues "
                              + (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "")
                              + "GROUP BY severity";

            var result = new Dictionary<Severity, int>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[(Severity)r.GetInt32(0)] = r.GetInt32(1);
            return result;
        }
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}
