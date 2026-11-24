<div align="center">

<img src="src/Fjord.App/Assets/fjord.png" width="96" alt="Fjord logo" />

# Fjord

**A modern SSH client for Windows — tabs, SFTP, tunnels and SSH keys, without the 1999 UI.**

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

</div>

Fjord is a PuTTY replacement built around the things you actually do every day: keep a tidy list of servers,
open a few terminals side by side, move files around, reach a database behind a firewall, and log in with
keys instead of passwords. It uses the Windows 11 Fluent look, and the terminal is
[xterm.js](https://xtermjs.org/) — the same terminal that powers VS Code — so colours, mouse support and UTF-8 just work.

## Features

### Sessions
- **Saved sessions in folders** (per customer, per project…), searchable, double-click or Enter to connect.
- **Quick connect** — type `user@host` or `host:2222` and press Enter.
- **Import from PuTTY** — reads your saved PuTTY sessions from the registry, including `.ppk` key paths.
- **Startup command** and **default SFTP folder** per session, plus keep-alives.

### Terminal
- xterm-256color, true colour, mouse, UTF-8 (æøå, emoji), Ctrl+click to open links.
- PuTTY-style clipboard: **select to copy, right-click to paste** (or Ctrl+Shift+C / Ctrl+Shift+V).
- Tabs: **Ctrl+Tab** to switch, **Ctrl+Shift+T** for another terminal on the same server, **Ctrl+Shift+W** to close.
- **Ctrl + / − / 0** to change font size. Press **Enter** in a dropped session to reconnect.
- **Colour schemes** — Campbell, One Half Dark/Light, Solarized Dark/Light, Tango, Gruvbox, Nord, Dracula, plus
  *Production (dark red)* and *Test (dark green)*. Pick a default in Settings and override it per session, so
  production servers always look different. Open terminals switch instantly.
- **Colour prompt** — many servers (root on Debian/Ubuntu, Alpine, minimal images) show a plain `user@host:path`.
  One click adds a small, marked block to your shell's start-up file (bash, zsh or BusyBox sh) that colours it with
  your scheme's colours — red when you're root. Click again to remove it.
- **Session logs** — the **Log** button saves everything a terminal shows to a file, or tick *Log this session* in the
  session editor to log every connection. Plain-text logs strip colour codes, keep only the final state of progress
  bars, replace full-screen programs (vim, top) with a note, and can put the time on every line; raw logs keep the
  exact bytes. Folder and file-name pattern (`{session}_{date}_{time}.log`) are set in Settings.
- **Snippets and macros** — saved commands in folders, from the **Snippets** button or **Ctrl+Shift+S**; the first
  nine also run with **Ctrl+Shift+1…9**. Several lines make a macro (each line runs in turn), and `{ask:Service}`
  asks for a value when it runs, e.g. `journalctl -u {ask:Service} -f`. Run it, or just type it so you can edit
  first. Manage them from the sidebar.

### SFTP file manager
- **Edit remote files in any Windows app** — open `nginx.conf` in Notepad++ or VS Code, and every save is uploaded
  back. If someone changed the file on the server meanwhile, you're asked before it's overwritten.
- **Drag and drop both ways** — drag files and folders in from Explorer to upload, or out to Explorer / the desktop
  to download. Drag onto a folder in the list to move (hold Ctrl to copy), or onto another server's tab to copy across.
- **Cut, copy and paste** on the server (Ctrl+X / C / V), also between tabs of the same server. Copies run `cp` on the
  server, so big files don't travel through your PC. Ctrl+V with files copied in Explorer uploads them.
- **Rename or move** (F2) — type a new name, or a path like `../archive/` to move it.
- **Permissions** — checkboxes or a number (644, 755…), optionally for everything inside a folder. Owner and group
  are shown by name (`root`, `www-data`) from the server's /etc/passwd and /etc/group.
- **Two panes (optional)** — turn on *Settings → SFTP → Two panes* to get this PC on the left and the server on
  the right. Drag between them, or use Upload / Download, which then go straight to the other pane's folder.
  Local deletes go to the Recycle Bin.
- New file, new folder, delete, download, copy path.
- Opens from a terminal tab using **the same login** — no second password prompt.

### Tunnels (port forwarding)
- Reach a database, admin page or Windows PC behind a server from your own PC.
- Start from examples — *PostgreSQL database*, *Remote Desktop to a PC on their network*, *Browse through the server* —
  and the editor explains in plain words, with a picture, what each setting does.
- Local, remote and SOCKS forwarding (`-L`, `-R`, `-D`). Tunnels can start automatically on connect and live in a side
  panel with *Open in browser* / *Remote Desktop* buttons.
- Failures are explained: port already in use, target unreachable, forwarding disabled on the server.

### SSH keys
- **Key generator** — Ed25519 (default) or RSA-4096 in native OpenSSH format, optional passphrase
  (aes256-ctr + bcrypt, same as `ssh-keygen`). Saved to `%USERPROFILE%\.ssh` with the permissions Windows OpenSSH requires.
- **Install key** — Fjord's `ssh-copy-id`: adds your public key to the server's `authorized_keys`, fixes permissions,
  skips duplicates, then offers to switch the session to key login.
- **Built-in guides** for keys and tunnels that open in their own window, so they never block your session.

### Security
- Host keys are verified: trust on first use, with a loud warning if a server's key changes.
- Remembered passwords are encrypted with Windows DPAPI — only your Windows user on that PC can read them.
- Reads OpenSSH, PEM and PuTTY `.ppk` private keys.

## Getting started

**Requirements:** Windows 10 or 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and the
WebView2 runtime (preinstalled on Windows 11 and up-to-date Windows 10).

```powershell
git clone https://github.com/kbrynj/fjord.git
cd fjord
dotnet run --project src\Fjord.App
```

Or open `Fjord.sln` in Visual Studio 2026, Rider or VS Code (C# Dev Kit) and press F5.

### Build a portable folder

```powershell
dotnet publish src\Fjord.App -c Release -r win-x64 --self-contained -o publish\Fjord
```

Copy `publish\Fjord` anywhere and run `Fjord.exe` — the target PC doesn't need .NET installed.
Drop `--self-contained` for a much smaller folder if .NET 10 is already there.

## Your data

Everything lives in `%APPDATA%\Fjord` (Settings → *Data folder*):

| File | Contents |
|---|---|
| `profiles.json` | Saved sessions and tunnels — back this up to keep your sessions. |
| `known_hosts.json` | Trusted server host keys. |
| `settings.json` | Terminal font, scrollback, clipboard behaviour, default username. |

Remembered passwords in `profiles.json` are DPAPI-encrypted and only decrypt for your Windows user on that PC.

## How it's built

- **.NET 10 + WPF** with the built-in Fluent theme
- **[SSH.NET](https://github.com/sshnet/SSH.NET)** for SSH, SFTP and port forwarding
- **[xterm.js](https://xtermjs.org/)** in **WebView2** for the terminal

```
src/
  Fjord.Core/     UI-free logic, unit-testable: sessions, key generation, SSH connection + host keys, tunnels,
                  remote file operations (copy, move, permissions)
  Fjord.App/      WPF app: main window, terminal / SFTP / tunnel views, dialogs, pluggable features
    Assets/       xterm.js page + bridge, built-in guides (plain HTML — edit freely)
```

The code deliberately stays simple: code-behind and a handful of singletons, no MVVM toolkit or DI container.

## Contributing

Issues and pull requests are welcome. Most new things are a button somewhere — implement `IAppFeature` and add
one line to `FeatureRegistry`:

```csharp
public sealed class UptimeFeature : IAppFeature
{
    public string Title => "Uptime";
    public string Glyph => "";                    // Segoe Fluent Icons
    public FeatureScope Scope => FeatureScope.Session;  // Session = terminal toolbar, Global = sidebar

    public void Execute(FeatureContext ctx)
    {
        if (ctx.Terminal?.Client is { } ssh)
            MessageBox.Show(ssh.RunCommand("uptime").Result);
    }
}
```

Through `FeatureContext` a feature can reach the main window (`OpenTerminal`, `OpenSftp`, `AddTab`, `ActiveTab`) and,
for session features, the terminal (`Profile`, the connected `Client`, its `Endpoint` for more channels with the same
login, and `Send("text")` to type into the shell).

- A new kind of tab is a `UserControl` implementing `ISessionTab`, opened with `ctx.Shell.AddTab(...)`.
- A new per-session setting is a property on `SessionProfile` (it persists automatically) plus a field in `ProfileDialog`.
- A new tunnel example ("Redis", "VNC"…) is one entry in `TunnelPresets.All`.

See [CLAUDE.md](CLAUDE.md) for the architecture rules (threading, persistence, the terminal bridge protocol).

### Roadmap ideas

Pageant / ssh-agent support · jump hosts.

## License

[MIT](LICENSE). Third-party components and their licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
