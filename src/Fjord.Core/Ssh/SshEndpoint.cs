using Fjord.Core.Profiles;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Fjord.Core.Ssh;

public sealed record PasswordAnswer(string Password, bool Remember);

/// <summary>Everything the connection logic needs to ask the user. Implemented by the UI.</summary>
public interface ISessionPrompts
{
    /// <returns>null if the user cancelled.</returns>
    PasswordAnswer? AskPassword(SessionProfile profile, string message);
    string? AskPassphrase(string keyPath, bool previousAttemptFailed);
    string? AskText(string title, string prompt, bool secret);
    /// <returns>true to trust and continue.</returns>
    bool ConfirmHostKey(SessionProfile profile, string keyType, string fingerprint, HostKeyStatus status, KnownHost? previous);
}

/// <summary>Encrypts remembered passwords (DPAPI on Windows).</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string? Unprotect(string protectedValue);
}

public sealed class SshConnectException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A resolved, authenticated way to reach one server. The first connect asks for
/// passwords/passphrases; after that, extra channels (SFTP, a second terminal, running a
/// command) reuse the same credentials without prompting again.
/// </summary>
public sealed class SshEndpoint : IDisposable
{
    private const int MaxAttempts = 3;

    private readonly ISessionPrompts _prompts;
    private readonly KnownHostsStore _knownHosts;
    private readonly ISecretProtector _protector;

    private string? _password;
    private PrivateKeyFile? _key;

    public SessionProfile Profile { get; }

    /// <summary>True when the profile was changed during connect (e.g. "remember password", username entered).</summary>
    public bool ProfileModified { get; private set; }

    public SshEndpoint(SessionProfile profile, ISessionPrompts prompts, KnownHostsStore knownHosts, ISecretProtector protector)
    {
        Profile = profile;
        _prompts = prompts;
        _knownHosts = knownHosts;
        _protector = protector;
    }

    /// <summary>Connects an SshClient, prompting as needed. Call from a background thread or await it.</summary>
    public Task<SshClient> ConnectSshAsync(CancellationToken ct = default) =>
        ConnectAsync(info => new SshClient(info), ct);

    public Task<SftpClient> ConnectSftpAsync(CancellationToken ct = default) =>
        ConnectAsync(info => new SftpClient(info), ct);

    private async Task<T> ConnectAsync<T>(Func<ConnectionInfo, T> factory, CancellationToken ct) where T : BaseClient
    {
        // Prompting must not happen on a thread that holds the UI — run the setup off-thread.
        await Task.Run(PrepareCredentials, ct).ConfigureAwait(false);

        for (int attempt = 1; ; attempt++)
        {
            var client = factory(BuildConnectionInfo());   // fresh per client: auth methods hold per-connection state
            bool hostKeyRejected = false;
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = VerifyHostKey(e);
                hostKeyRejected = !e.CanTrust;
            };
            if (Profile.KeepAliveSeconds > 0)
                client.KeepAliveInterval = TimeSpan.FromSeconds(Profile.KeepAliveSeconds);

            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                return client;
            }
            catch (SshAuthenticationException) when (attempt < MaxAttempts)
            {
                client.Dispose();
                var answer = await Task.Run(() => _prompts.AskPassword(Profile, "Access denied. Enter password:"), ct).ConfigureAwait(false)
                             ?? throw new OperationCanceledException();
                SetPassword(answer);
            }
            catch (SshAuthenticationException ex)
            {
                client.Dispose();
                throw new SshConnectException("Authentication failed: " + ex.Message, ex);
            }
            catch (Exception) when (hostKeyRejected)
            {
                client.Dispose();
                throw new SshConnectException("Connection aborted: the host key was not trusted.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                client.Dispose();
                throw new SshConnectException(Describe(ex), ex);
            }
        }
    }

    private void PrepareCredentials()
    {
        if (string.IsNullOrWhiteSpace(Profile.Username))
        {
            Profile.Username = _prompts.AskText("Login", $"Login as (user on {Profile.Host}):", secret: false)?.Trim()
                               ?? throw new OperationCanceledException();
            ProfileModified = true;
        }

        if (Profile.Auth == AuthMethod.PrivateKey && _key is null && !string.IsNullOrWhiteSpace(Profile.PrivateKeyPath))
            _key = LoadKey(Profile.PrivateKeyPath);

        if (_password is null && Profile.ProtectedPassword is { } saved)
            _password = _protector.Unprotect(saved);

        // Password logins need the password up front (SSH.NET's "password" method takes it in the constructor).
        if (Profile.Auth == AuthMethod.Password && _password is null)
        {
            var answer = _prompts.AskPassword(Profile, $"Password for {Profile.Target}:") ?? throw new OperationCanceledException();
            SetPassword(answer);
        }
    }

    private void SetPassword(PasswordAnswer answer)
    {
        _password = answer.Password;
        if (answer.Remember)
        {
            Profile.ProtectedPassword = _protector.Protect(answer.Password);
            ProfileModified = true;
        }
        else if (Profile.ProtectedPassword is not null)
        {
            Profile.ProtectedPassword = null;   // the saved one was wrong / user un-ticked remember
            ProfileModified = true;
        }
    }

    private PrivateKeyFile LoadKey(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(path)) throw new SshConnectException($"Private key not found: {path}");

        try
        {
            return new PrivateKeyFile(path);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            for (int i = 0; i < MaxAttempts; i++)
            {
                var pass = _prompts.AskPassphrase(path, previousAttemptFailed: i > 0) ?? throw new OperationCanceledException();
                try { return new PrivateKeyFile(path, pass); }
                catch (SshException) { /* wrong passphrase → ask again */ }
            }
            throw new SshConnectException("Wrong passphrase for " + Path.GetFileName(path));
        }
        catch (SshException ex)
        {
            throw new SshConnectException($"Could not read key {Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        var user = Profile.Username;
        var methods = new List<AuthenticationMethod>();

        if (_key is not null)
            methods.Add(new PrivateKeyAuthenticationMethod(user, _key));

        if (_password is not null)
            methods.Add(new PasswordAuthenticationMethod(user, _password));

        // Keyboard-interactive covers PAM prompts, 2FA codes, and "password" when the server
        // only offers keyboard-interactive. Known password prompts are answered automatically.
        var kbd = new KeyboardInteractiveAuthenticationMethod(user);
        kbd.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (_password is not null && !prompt.IsEchoed && prompt.Request.Contains("assword", StringComparison.OrdinalIgnoreCase))
                    prompt.Response = _password;
                else
                    prompt.Response = _prompts.AskText(string.IsNullOrWhiteSpace(e.Instruction) ? Profile.DisplayName : e.Instruction,
                                                       prompt.Request, secret: !prompt.IsEchoed) ?? "";
            }
        };
        methods.Add(kbd);

        return new ConnectionInfo(Profile.Host, Profile.Port, user, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    private bool VerifyHostKey(HostKeyEventArgs e)
    {
        var fp = "SHA256:" + e.FingerPrintSHA256;
        var status = _knownHosts.Check(Profile.Host, Profile.Port, e.HostKeyName, fp, out var previous);
        if (status == HostKeyStatus.Trusted) return true;

        if (!_prompts.ConfirmHostKey(Profile, e.HostKeyName, fp, status, previous)) return false;
        _knownHosts.Trust(Profile.Host, Profile.Port, e.HostKeyName, fp);
        return true;
    }

    private string Describe(Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException se => $"Could not connect to {Profile.Host}:{Profile.Port} — {se.Message}",
        SshOperationTimeoutException => $"Timed out connecting to {Profile.Host}:{Profile.Port}.",
        _ => ex.Message,
    };

    public void Dispose()
    {
        _key?.Dispose();
        _password = null;
    }
}
