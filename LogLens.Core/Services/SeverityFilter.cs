using LogLens.Models;

namespace LogLens.Services;

/// <summary>Which severity classes the chips currently allow. Debug and Trace share a chip.</summary>
[Flags]
public enum SeverityClasses
{
    None  = 0,
    Debug = 1,
    Info  = 2,
    Warn  = 4,
    Error = 8,
    Fatal = 16,
    All   = Debug | Info | Warn | Error | Fatal
}

/// <summary>
/// The severity-chip filter. Kept as a pure function so it can be tested without a
/// dispatcher: the view-models feed lines through in order and thread the carry flag.
///
/// The carry flag is the subtle part. Unclassified lines — stack frames, exception
/// headers, wrapped continuations — carry no severity of their own, so they follow
/// the visibility of the classified line above them. Filtering to errors keeps each
/// error's stack trace attached instead of orphaning the error on its own.
/// </summary>
public static class SeverityFilter
{
    public static SeverityClasses ClassOf(Severity severity) => severity switch
    {
        Severity.Fatal => SeverityClasses.Fatal,
        Severity.Error => SeverityClasses.Error,
        Severity.Warn  => SeverityClasses.Warn,
        Severity.Info  => SeverityClasses.Info,
        Severity.Debug or Severity.Trace => SeverityClasses.Debug,
        _ => SeverityClasses.None
    };

    /// <summary>
    /// Whether a line passes the chip mask. <paramref name="carry"/> holds the
    /// visibility of the most recent classified line and must persist across calls
    /// in line order; start it at true so leading unclassified lines stay visible.
    /// </summary>
    public static bool Passes(Severity severity, SeverityClasses mask, ref bool carry)
    {
        if (mask == SeverityClasses.All)
        {
            carry = true;
            return true;
        }

        var cls = ClassOf(severity);
        if (cls == SeverityClasses.None) return carry;

        carry = (mask & cls) != 0;
        return carry;
    }
}

/// <summary>
/// Carry state per source file. A single carry works for one file, but the merged
/// timeline interleaves sources — file B's INFO can land between file A's error and
/// A's stack trace, and a single carry would then attach A's trace to B's INFO.
/// Keying the carry by the line's SourceIndex follows each file's own thread of
/// continuation regardless of how the merged buffer interleaves or when batches
/// arrive. For a plain file tab every line shares one index, so it degrades to the
/// single-carry behaviour exactly.
/// </summary>
public sealed class SeverityCarryMap
{
    private readonly Dictionary<int, bool> _carry = new();

    public void Reset() => _carry.Clear();

    public bool Passes(int sourceIndex, Severity severity, SeverityClasses mask)
    {
        bool carry = !_carry.TryGetValue(sourceIndex, out var known) || known;
        bool pass = SeverityFilter.Passes(severity, mask, ref carry);
        _carry[sourceIndex] = carry;
        return pass;
    }
}
