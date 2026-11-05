using System.Globalization;

namespace CeratoSoundAI.App.Controls;

public sealed class NotConverter : IValueConverter
{
    public object? Convert(object? v, Type t, object? p, CultureInfo c) => v is bool b ? !b : true;
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is bool b ? !b : false;
}

public sealed class StarConverter : IValueConverter
{
    public object? Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? "★" : "☆";
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class UserBgConverter : IValueConverter
{
    public object? Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? Color.FromArgb("#1B3042") : Color.FromArgb("#26384B");
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
