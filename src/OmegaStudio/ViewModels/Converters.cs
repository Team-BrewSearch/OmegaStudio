using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;
namespace OmegaStudio.ViewModels;

internal static class PaletteLookup
{
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
public sealed class AssetKindBrushConverter : IValueConverter
{
    private static string KeyFor(string? kind)
    {
        return string.IsNullOrEmpty(kind) ? Fallback : $"Asset{kind}Brush";
    }
    private const string Fallback = "AssetUnknownBrush";
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = KeyFor(value as string);
        return PaletteLookup.Find(key) ?? PaletteLookup.Find(Fallback) ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
