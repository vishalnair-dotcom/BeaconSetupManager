using System.Windows;
using System.Windows.Controls;

namespace BeaconSetupManager.Services;

public static class PasswordBoxBinding
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached("Password", typeof(string), typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static string GetPassword(DependencyObject target) => (string?)target.GetValue(PasswordProperty) ?? "";
    public static void SetPassword(DependencyObject target, string value) => target.SetValue(PasswordProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not PasswordBox box) return;
        box.PasswordChanged -= PasswordChanged;
        string value = (string?)args.NewValue ?? "";
        if (box.Password != value) box.Password = value;
        box.PasswordChanged += PasswordChanged;
    }
    private static void PasswordChanged(object sender, RoutedEventArgs args)
    {
        var box = (PasswordBox)sender;
        box.SetCurrentValue(PasswordProperty, box.Password);
    }
}
