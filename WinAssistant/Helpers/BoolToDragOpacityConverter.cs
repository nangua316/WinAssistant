using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinAssistant.Helpers;

public class BoolToDragOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? 0.4 : 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
