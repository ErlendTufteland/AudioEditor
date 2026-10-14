using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AudioEditor.App.Controls;

/// <summary>
/// For text boxes bound with UpdateSourceTrigger=Explicit: Enter or leaving the box applies
/// the text, Escape reverts it. Afterwards the box shows the value that was actually stored,
/// so rejected or clamped input never lingers.
/// </summary>
public static class TextBoxCommit
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TextBoxCommit), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        if ((bool)e.NewValue)
        {
            box.KeyDown += OnKeyDown;
            box.LostKeyboardFocus += OnLostFocus;
        }
        else
        {
            box.KeyDown -= OnKeyDown;
            box.LostKeyboardFocus -= OnLostFocus;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            Commit(box);
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            box.SelectAll();
            e.Handled = true;
        }
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit((TextBox)sender);

    /// <summary>Applies the text, as Enter would. Shortcuts such as Ctrl+S use this to include what is being typed.</summary>
    public static void Commit(TextBox box)
    {
        var binding = box.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource();
        binding?.UpdateTarget();
    }
}
