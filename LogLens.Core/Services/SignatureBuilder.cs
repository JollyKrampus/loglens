using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LogLens.Services;

/// <summary>What a raw log occurrence was distilled into.</summary>
public sealed record IssueFingerprint(
    string Hash,
    string Signature,
    string Title,
    string? ExceptionType,
    string? FaultingMethod,
    string? Logger);

/// <summary>
/// Turns a log occurrence into a stable identity, so the same fault logged ten
/// thousand times collapses into one issue you can raise a ticket for.
///
/// This is deliberately deterministic rather than model-driven. Log lines are far
/// more structured than they look: a .NET exception gives you a type, a message and
/// a stack, and NLog gives you a logger name. Masking the parts that vary per
/// occurrence — timestamps, ids, durations, counts, paths — leaves a skeleton that
/// is the same for every instance of the same bug. That gets you accurate grouping
/// and a usable ticket title with no network call and no per-line cost.
/// </summary>
public static class SignatureBuilder
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Order matters: the more specific patterns run first so a GUID is not first
    // chewed up by the hex-number rule.
    private static readonly (Regex Pattern, string Replacement)[] Masks =
    [
        (new Regex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?", Opts), "<ts>"),
        (new Regex(@"\b\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\b", Opts), "<time>"),
        (new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", Opts), "<guid>"),
        (new Regex(@"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?::\d+)?\b", Opts), "<ip>"),
        (new Regex(@"\b[A-Za-z]:\\[^\s""'<>|]+", Opts), "<path>"),
        (new Regex(@"\\\\[^\s""'<>|]+", Opts), "<path>"),
        (new Regex(@"\bhttps?://[^\s""'<>]+", Opts), "<url>"),
        (new Regex(@"\b[\w.+-]+@[\w-]+\.[\w.-]+\b", Opts), "<email>"),
        (new Regex(@"\b0x[0-9a-fA-F]+\b", Opts), "<hex>"),
        (new Regex(@"\b[0-9a-fA-F]{16,}\b", Opts), "<hex>"),
        (new Regex(@"'[^']*'", Opts), "'<s>'"),
        (new Regex(@"""[^""]*""", Opts), "\"<s>\""),
        (new Regex(@"\b\d+(?:\.\d+)?(?:ms|s|kb|mb|gb|%)\b", Opts | RegexOptions.IgnoreCase), "<n>"),
        (new Regex(@"\b\d[\d,._]*\b", Opts), "<n>"),
    ];

    // "2026-08-14 12:52:40.0472|ERROR|Acme.Orders.OrderService|Timeout calling..."
    private static readonly Regex PipeLayout =
        new(@"^[^|]*\|\s*(?<level>[A-Z]{3,8})\s*\|(?<logger>[^|]*)\|(?<msg>.*)$", Opts);

    // "System.Net.Http.HttpRequestException: The operation timed out."
    private static readonly Regex ExceptionHeader =
        new(@"(?<type>\b[A-Z]\w*(?:\.\w+)*Exception)\s*:\s*(?<msg>.*)$", Opts);

    // "   at Acme.Payments.PaymentClient.ChargeAsync(ChargeRequest r)"
    private static readonly Regex StackFrame =
        new(@"^\s+at\s+(?<method>[^\s(]+)", Opts);

    // Serilog-ish "[12:52:41 ERR] Acme.Orders Something failed"
    private static readonly Regex BracketLevel =
        new(@"^\W{0,2}[\d:.]+\s+(?<level>[A-Z]{3})\]?\s*(?<rest>.*)$", Opts);

    /// <summary>
    /// Builds a fingerprint from the triggering line plus any continuation lines
    /// (exception header, stack frames) that belong to it.
    /// </summary>
    public static IssueFingerprint Build(string line, IReadOnlyList<string>? continuation = null)
    {
        var message = ExtractMessage(line, out var logger);

        string? exceptionType = null;
        string? faultingMethod = null;

        // An exception type on the triggering line itself.
        var inline = ExceptionHeader.Match(line);
        if (inline.Success) exceptionType = inline.Groups["type"].Value;

        if (continuation is not null)
        {
            foreach (var extra in continuation)
            {
                if (exceptionType is null)
                {
                    var m = ExceptionHeader.Match(extra);
                    if (m.Success) exceptionType = m.Groups["type"].Value;
                }

                if (faultingMethod is null)
                {
                    var f = StackFrame.Match(extra);
                    // The first frame is the method that actually threw.
                    if (f.Success) faultingMethod = f.Groups["method"].Value;
                }

                if (exceptionType is not null && faultingMethod is not null) break;
            }
        }

        // The signature is the masked message, plus the exception identity when we
        // have one — two different faults can share a generic message like "failed".
        var maskedMessage = Mask(message);
        var signature = new StringBuilder();
        if (exceptionType is not null) signature.Append(exceptionType).Append(" | ");
        if (faultingMethod is not null) signature.Append(faultingMethod).Append(" | ");
        if (!string.IsNullOrWhiteSpace(logger)) signature.Append(logger.Trim()).Append(" | ");
        signature.Append(maskedMessage);

        var sig = signature.ToString().Trim();
        return new IssueFingerprint(Hash(sig), sig, BuildTitle(exceptionType, faultingMethod, logger, maskedMessage),
            exceptionType, faultingMethod, string.IsNullOrWhiteSpace(logger) ? null : logger.Trim());
    }

    /// <summary>Replaces the per-occurrence noise with placeholders.</summary>
    public static string Mask(string text)
    {
        var s = text;
        foreach (var (pattern, replacement) in Masks) s = pattern.Replace(s, replacement);

        // Collapse whitespace so formatting differences don't split a group.
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Strips the timestamp/level/logger preamble, returning just the message.</summary>
    private static string ExtractMessage(string line, out string? logger)
    {
        logger = null;

        var pipe = PipeLayout.Match(line);
        if (pipe.Success)
        {
            logger = pipe.Groups["logger"].Value;
            return pipe.Groups["msg"].Value.Trim();
        }

        var bracket = BracketLevel.Match(line);
        if (bracket.Success) return bracket.Groups["rest"].Value.Trim();

        // Space-delimited "<ts> LEVEL Component message" — drop the leading timestamp
        // and level token, keep the rest.
        var generic = Regex.Match(line,
            @"^\s*(?:\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\s+)?(?:FATAL|ERROR|ERR|WARN|WARNING|INFO|DEBUG|TRACE)\s+(?<rest>.*)$",
            RegexOptions.IgnoreCase);

        return generic.Success ? generic.Groups["rest"].Value.Trim() : line.Trim();
    }

    /// <summary>
    /// A ticket title someone can read in a backlog. Prefers the exception type and
    /// the method that threw, because that pair identifies a bug far better than a
    /// message that may just say "Operation failed".
    /// </summary>
    private static string BuildTitle(string? exceptionType, string? faultingMethod, string? logger, string maskedMessage)
    {
        // A type wants only its final segment ("HttpRequestException"), while a method
        // needs the declaring class too ("PaymentClient.ChargeAsync") to mean anything.
        var shortType = LastSegment(exceptionType);
        var shortMethod = LastSegments(faultingMethod, 2);
        var shortLogger = LastSegment(logger);

        string title;
        if (shortType is not null && shortMethod is not null)
            title = $"{shortType} in {shortMethod}";
        else if (shortType is not null)
            title = shortType;
        else if (shortLogger is not null)
            title = shortLogger;
        else
            title = "";

        // The message has to be part of the title, not just the signature. Two faults
        // can share an exception type and faulting method and still be different bugs;
        // without the message the list shows rows that are indistinguishable by eye.
        if (!string.IsNullOrWhiteSpace(maskedMessage)
            && !title.Contains(maskedMessage, StringComparison.OrdinalIgnoreCase))
        {
            title = title.Length == 0 ? maskedMessage : $"{title} — {maskedMessage}";
        }

        title = title.Trim();
        if (title.Length > 120) title = title[..117].TrimEnd() + "…";
        return title.Length == 0 ? "(empty log line)" : title;
    }

    /// <summary>"System.Net.Http.HttpRequestException" -> "HttpRequestException".</summary>
    private static string? LastSegment(string? dotted) => LastSegments(dotted, 1);

    /// <summary>"Acme.Payments.PaymentClient.ChargeAsync" -> "PaymentClient.ChargeAsync".</summary>
    private static string? LastSegments(string? dotted, int count)
    {
        if (string.IsNullOrWhiteSpace(dotted)) return null;

        var parts = dotted.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        return parts.Length <= count ? string.Join('.', parts) : string.Join('.', parts[^count..]);
    }

    private static string Hash(string signature)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexString(bytes, 0, 10).ToLowerInvariant();
    }

    /// <summary>
    /// True when a line is a continuation of the one above rather than a new event —
    /// a stack frame, an exception header, or an inner-exception marker.
    /// </summary>
    public static bool IsContinuation(string line)
        => StackFrame.IsMatch(line)
           || line.Contains("--->", StringComparison.Ordinal)
           || line.Contains("End of inner exception", StringComparison.Ordinal)
           || ExceptionHeader.IsMatch(line);
}
