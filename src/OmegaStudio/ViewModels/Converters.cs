using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using System.Globalization;

namespace OmegaStudio.ViewModels;

/// <summary>Resolves a brush from the application's resources by key.</summary>
internal static class PaletteLookup
{
    /// <summary>
    /// The brush <paramref name="key"/> names in the live theme variant, or null when the
    /// theme has no such brush.
    /// </summary>
    public static IBrush? Find(string key)
    {
        Application? app = Application.Current;
        return app is not null
            && app.TryFindResource(key, app.ActualThemeVariant, out object? found)
            && found is IBrush brush
                ? brush
                : null;
    }
}

/// <summary>
/// Maps an Outliner row's asset kind to its colour dot.
///
/// <para>
/// Safe to resolve once per row despite the theme being changeable at runtime:
/// the asset-kind colours in <c>Themes/Palette.axaml</c> are deliberately
/// variant-independent, because a categorical colour has to mean the same thing
/// in light and dark. Anything that *does* vary by theme is bound with
/// <c>DynamicResource</c> or a style class instead of coming through here.
/// </para>
/// </summary>
public sealed class AssetKindBrushConverter : IValueConverter
{
    /// <summary>The palette key a kind draws with, e.g. <c>Model</c> to <c>AssetModelBrush</c>.</summary>
    private static string KeyFor(string? kind)
    {
        return string.IsNullOrEmpty(kind) ? Fallback : $"Asset{kind}Brush";
    }

    private const string Fallback = "AssetUnknownBrush";

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Derived rather than looked up in a table. A kind the palette has no colour
        // for falls back to the neutral one, so a plugin inventing its own kind works
        // and adding a colour is one entry in Palette.axaml rather than three edits.
        // One lookup per outcome: asking whether the key exists and then asking for it
        // walked the resource dictionaries twice for every row in the Outliner.
        string key = KeyFor(value as string);
        return PaletteLookup.Find(key) ?? PaletteLookup.Find(Fallback) ?? Brushes.Gray;
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
