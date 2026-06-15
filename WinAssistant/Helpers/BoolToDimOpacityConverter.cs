using Microsoft.UI.Xaml.Data;

namespace WinAssistant.Helpers;

public class BoolToDimOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 0.5 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
