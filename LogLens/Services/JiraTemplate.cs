using System.Text;
using LogLens.Models;

namespace LogLens.Services;

/// <summary>
/// Builds a ready-to-paste Jira ticket from an aggregated issue.
///
/// No model call and no network. Everything below is already present in the log:
/// the exception type, the method that threw, the logger, how often it has
/// happened, when it started, and which environments it affects. Assembling that
/// into the shape a ticket wants is formatting, not inference — and it stays
/// identical every time, which a generated summary would not.
/// </summary>
public static class JiraTemplate
{
    /// <summary>The one-line summary field.</summary>
    public static string Summary(LogIssue issue, string? projectKey = null)
    {
        var prefix = issue.Severity switch
        {
            Severity.Fatal => "[FATAL] ",
            Severity.Error => "[ERROR] ",
            Severity.Warn => "[WARN] ",
            _ => ""
        };

        var summary = prefix + issue.Title;
        return summary.Length > 240 ? summary[..237].TrimEnd() + "…" : summary;
    }

    /// <summary>
    /// The description field, in Jira wiki markup. Jira's rich editor accepts a
    /// paste of this and renders the headings, table and code blocks.
    /// </summary>
    public static string Description(LogIssue issue)
    {
        var sb = new StringBuilder();

        sb.AppendLine("h3. What is happening");
        sb.AppendLine();
        sb.AppendLine(Escape(issue.Title));
        sb.AppendLine();

        sb.AppendLine("h3. Evidence");
        sb.AppendLine();
        sb.AppendLine("||Field||Value||");
        sb.AppendLine($"|Severity|{issue.Severity}|");
        sb.AppendLine($"|Occurrences|{issue.Count:N0}|");
        sb.AppendLine($"|First seen|{issue.FirstSeenLocal:yyyy-MM-dd HH:mm:ss}|");
        sb.AppendLine($"|Last seen|{issue.LastSeenLocal:yyyy-MM-dd HH:mm:ss}|");
        sb.AppendLine($"|Duration|{Describe(issue.LastSeenLocal - issue.FirstSeenLocal)}|");

        if (!string.IsNullOrWhiteSpace(issue.Views))
            sb.AppendLine($"|Environments|{Escape(issue.Views)}|");
        if (!string.IsNullOrWhiteSpace(issue.Sources))
            sb.AppendLine($"|Log files|{Escape(issue.Sources)}|");
        if (!string.IsNullOrWhiteSpace(issue.ExceptionType))
            sb.AppendLine($"|Exception|{{{{{issue.ExceptionType}}}}}|");
        if (!string.IsNullOrWhiteSpace(issue.FaultingMethod))
            sb.AppendLine($"|Faulting method|{{{{{issue.FaultingMethod}}}}}|");
        if (!string.IsNullOrWhiteSpace(issue.Logger))
            sb.AppendLine($"|Logger|{{{{{issue.Logger}}}}}|");

        sb.AppendLine();
        sb.AppendLine("h3. Sample occurrence");
        sb.AppendLine();
        sb.AppendLine("{code}");
        sb.AppendLine(issue.SampleLine);
        if (!string.IsNullOrWhiteSpace(issue.SampleDetail)) sb.AppendLine(issue.SampleDetail.TrimEnd());
        sb.AppendLine("{code}");
        sb.AppendLine();

        sb.AppendLine("h3. How these were grouped");
        sb.AppendLine();
        sb.AppendLine("Occurrences were matched on this normalised signature, with timestamps, ids, "
                      + "paths and numbers masked, so every instance of this fault counts as one issue:");
        sb.AppendLine();
        sb.AppendLine("{code}");
        sb.AppendLine(issue.Signature);
        sb.AppendLine("{code}");
        sb.AppendLine();

        sb.AppendLine("h3. Still to establish");
        sb.AppendLine();
        sb.AppendLine("* Impact: who or what is affected when this fires?");
        sb.AppendLine("* Trigger: what request or job produces it?");
        sb.AppendLine("* Whether the retry/fallback path recovers, or work is lost.");
        sb.AppendLine();
        sb.AppendLine($"_Collected by LogLens. Signature {issue.Hash}._");

        return sb.ToString().TrimEnd();
    }

    /// <summary>Summary and description together, for a single clipboard copy.</summary>
    public static string Full(LogIssue issue, string? projectKey = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(projectKey)) sb.AppendLine($"Project: {projectKey}");
        sb.AppendLine("Issue type: Bug");
        sb.AppendLine($"Summary: {Summary(issue, projectKey)}");
        sb.AppendLine();
        sb.AppendLine("Description:");
        sb.AppendLine(Description(issue));
        return sb.ToString();
    }

    /// <summary>Plain-text variant for trackers that don't speak Jira markup.</summary>
    public static string PlainText(LogIssue issue)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary(issue));
        sb.AppendLine(new string('=', Math.Min(70, Summary(issue).Length)));
        sb.AppendLine();
        sb.AppendLine($"Severity     : {issue.Severity}");
        sb.AppendLine($"Occurrences  : {issue.Count:N0}");
        sb.AppendLine($"First seen   : {issue.FirstSeenLocal:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Last seen    : {issue.LastSeenLocal:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(issue.Views)) sb.AppendLine($"Environments : {issue.Views}");
        if (!string.IsNullOrWhiteSpace(issue.Sources)) sb.AppendLine($"Log files    : {issue.Sources}");
        if (!string.IsNullOrWhiteSpace(issue.ExceptionType)) sb.AppendLine($"Exception    : {issue.ExceptionType}");
        if (!string.IsNullOrWhiteSpace(issue.FaultingMethod)) sb.AppendLine($"Method       : {issue.FaultingMethod}");
        sb.AppendLine();
        sb.AppendLine("Sample:");
        sb.AppendLine(issue.SampleLine);
        if (!string.IsNullOrWhiteSpace(issue.SampleDetail)) sb.AppendLine(issue.SampleDetail.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"Signature: {issue.Signature}");
        return sb.ToString();
    }

    /// <summary>
    /// Deep link to the "create issue" screen, so you land on a pre-filled form.
    /// Jira caps URL length, so only the summary is passed — the description goes
    /// via the clipboard.
    /// </summary>
    public static string? CreateUrl(LogIssue issue, string? baseUrl, string? projectKey)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        var root = baseUrl.TrimEnd('/');
        var summary = Uri.EscapeDataString(Summary(issue, projectKey));

        var url = $"{root}/secure/CreateIssueDetails!default.jspa?issuetype=1&summary={summary}";
        if (!string.IsNullOrWhiteSpace(projectKey)) url += $"&pid=&project={Uri.EscapeDataString(projectKey)}";
        return url;
    }

    public static string? BrowseUrl(string? baseUrl, string? jiraKey)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(jiraKey)) return null;
        return $"{baseUrl.TrimEnd('/')}/browse/{Uri.EscapeDataString(jiraKey.Trim())}";
    }

    private static string Escape(string s) => s.Replace("|", "\\|");

    private static string Describe(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "under a minute";
        if (span.TotalHours < 1) return $"{span.TotalMinutes:N0} minutes";
        if (span.TotalDays < 1) return $"{span.TotalHours:N1} hours";
        return $"{span.TotalDays:N1} days";
    }
}
