using Microsoft.UI.Xaml.Data;

namespace WinAssistant.Helpers;

public class BoolToScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 1.08 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
