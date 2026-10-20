using System.Windows;

namespace EditorHelper.Views;

/// <summary>
/// Подтверждение опасного действия в стиле приложения: тёмная карточка вместо
/// системного MessageBox. Возвращает true только при явном «Подтвердить» —
/// Esc, клик вне окна и крестик окна считаются отказом.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string? hint = null, string confirmLabel = "Подтвердить")
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;

        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => CancelButton.Focus();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
