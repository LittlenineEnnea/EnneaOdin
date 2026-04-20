using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace EnneaOdin.Converters;

/// <summary>
/// true  → #50FF80 (green)
/// false → #FF6060 (red)
/// Used for heimdall status indicator.
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    private static readonly SolidColorBrush Green = new(Color.Parse("#50E880"));
    private static readonly SolidColorBrush Red   = new(Color.Parse("#FF6060"));
    private static readonly SolidColorBrush Grey  = new(Color.Parse("#556688"));

    public object Convert(object? value, Type t, object? param, CultureInfo c)
        => value is bool b ? (b ? Green : Red) : Grey;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotImplementedException();
}
