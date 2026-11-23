using System.IO;
using System.Windows;
using Fjord.Core.Profiles;
using Fjord.Core.Ssh;
using Fjord.Dialogs;

namespace Fjord.Services;

/// <summary>Shows connection prompts as dialogs. Called from background threads — marshals to the UI thread.</summary>
public sealed class UiPrompts(Window owner) : ISessionPrompts
{
    private T OnUi<T>(Func<T> f) => owner.Dispatcher.CheckAccess() ? f() : owner.Dispatcher.Invoke(f);

    public PasswordAnswer? AskPassword(SessionProfile profile, string message) => OnUi(() =>
    {
        var dlg = new InputDialog("Password — " + profile.DisplayName, message, isSecret: true,
                                  checkboxText: "Remember password (encrypted for your Windows user)")
        { Owner = owner, CheckboxValue = profile.ProtectedPassword is not null };
        return dlg.ShowDialog() == true ? new PasswordAnswer(dlg.Value, dlg.CheckboxValue) : null;
    });

    public string? AskPassphrase(string keyPath, bool previousAttemptFailed) => OnUi(() =>
    {
        var msg = (previousAttemptFailed ? "Wrong passphrase, try again.\n" : "") + $"Passphrase for key {Path.GetFileName(keyPath)}:";
        var dlg = new InputDialog("Key passphrase", msg, isSecret: true) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    });

    public string? AskText(string title, string prompt, bool secret) => OnUi(() =>
    {
        var dlg = new InputDialog(title, prompt, secret) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    });

    public bool ConfirmHostKey(SessionProfile profile, string keyType, string fingerprint, HostKeyStatus status, KnownHost? previous) => OnUi(() =>
    {
        if (status == HostKeyStatus.Changed)
        {
            var warn =
                $"WARNING — THE HOST KEY FOR {profile.Host}:{profile.Port} HAS CHANGED!\n\n" +
                $"Previously: {previous?.KeyType} {previous?.Fingerprint}\n" +
                $"Now:        {keyType} {fingerprint}\n\n" +
                "This happens when a server is reinstalled — or when someone is intercepting the connection.\n" +
                "Only continue if you know why the key changed.\n\nTrust the new key and connect?";
            return MessageBox.Show(owner, warn, "Host key changed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                   == MessageBoxResult.Yes;
        }

        var text =
            $"First connection to {profile.Host}:{profile.Port}.\n\n" +
            $"Host key: {keyType}\nFingerprint: {fingerprint}\n\n" +
            "If you can, compare this with the server (ssh-keygen -lf /etc/ssh/ssh_host_*_key.pub).\n\n" +
            "Trust this host and connect?";
        return MessageBox.Show(owner, text, "New host", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
               == MessageBoxResult.Yes;
    });
}
