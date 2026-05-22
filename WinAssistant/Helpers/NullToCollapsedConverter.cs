using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinAssistant.Helpers;

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value != null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
