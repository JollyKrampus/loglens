using System.Collections.Concurrent;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using LogLens.Core;

namespace LogLens.Avalonia;

/// <summary>Avalonia's side of the dispatcher abstraction the view-models depend on.</summary>
public sealed class AvaloniaUiThread : IUiThread
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>
/// The Avalonia twins of the WPF converters: hex to a cached brush, and Bold to a
/// font weight. Same caching rationale — colours are per-rule, rows are per-200k.
/// </summary>
public static class Converters
{
    private static readonly ConcurrentDictionary<string, IBrush?> Cache = new();

    public static IBrush? Resolve(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        return Cache.GetOrAdd(hex, static h =>
        {
            try { return new ImmutableSolidColorBrush(Color.Parse(h)); }
            catch { return null; }
        });
    }

    /// <summary>Hex to brush; null lets the theme foreground show through.</summary>
    public static readonly IValueConverter HexBrush =
        new FuncValueConverter<string?, IBrush?>(hex => Resolve(hex));

    /// <summary>Hex to brush with transparent fallback, for row backgrounds.</summary>
    public static readonly IValueConverter HexBrushOrTransparent =
        new FuncValueConverter<string?, IBrush>(hex => Resolve(hex) ?? Brushes.Transparent);

    public static readonly IValueConverter BoldWeight =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);
}
