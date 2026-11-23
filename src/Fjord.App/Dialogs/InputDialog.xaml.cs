using System.Windows;

namespace Fjord.Dialogs;

/// <summary>General one-field prompt (text or secret), with an optional checkbox.</summary>
public partial class InputDialog : Window
{
    private readonly bool _secret;

    public InputDialog(string title, string prompt, bool isSecret = false, string? checkboxText = null, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        _secret = isSecret;

        if (isSecret)
        {
            TextField.Visibility = Visibility.Collapsed;
            PasswordInput.Visibility = Visibility.Visible;
        }
        else
        {
            TextField.Text = initialValue;
            TextField.SelectAll();
        }

        if (checkboxText is not null)
        {
            OptionCheck.Content = checkboxText;
            OptionCheck.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => { if (_secret) PasswordInput.Focus(); else TextField.Focus(); };
    }

    public string Value => _secret ? PasswordInput.Password : TextField.Text;

    public bool CheckboxValue
    {
        get => OptionCheck.IsChecked == true;
        set => OptionCheck.IsChecked = value;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
