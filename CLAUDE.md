# Fjord — notes for Claude Code

Windows SSH client (PuTTY replacement). .NET 10, WPF with the built-in Fluent theme (`ThemeMode`, set in `App.xaml.cs`),
SSH.NET 2026.0.1, xterm.js 6 in WebView2. Personal tool: prefer simple, readable code over frameworks
(no MVVM toolkit, no DI container — code-behind + `AppServices` singletons is intentional).

## Build

`dotnet build Fjord.sln` (Windows only for Fjord.App; Fjord.Core builds anywhere).

## Architecture rules

- **Fjord.Core has no UI references.** Anything needing the user (passwords, host key confirmation) goes through
  `ISessionPrompts`; secrets through `ISecretProtector`. Keep it that way so Core stays testable.
- **All connections go through `SshEndpoint`.** It prompts once, then reuses the credentials for SFTP / extra channels.
  Never construct `SshClient`/`SftpClient` directly in the UI — host-key verification lives in the endpoint.
- **Tabs**: `ISessionTab` UserControls hosted in `MainWindow.ContentHost` and shown/hidden (not inside the TabControl's
  content), because removing a WebView2 from the visual tree tears it down.
- **Features**: new commands implement `IAppFeature` and are listed in `FeatureRegistry`. `Session` scope = terminal
  toolbar, `Global` = sidebar tools.
- **Terminal bridge**: `Assets/terminal/terminal.js` ⇄ `TerminalTab.OnWebMessage`. Messages are JSON `{ t: "...", ... }`;
  the protocol is documented at the top of terminal.js. Output is base64 bytes (xterm decodes UTF-8 itself).
- **Threading**: SSH.NET calls are blocking → `Task.Run`. Prompts are marshalled with `Dispatcher.Invoke` in `UiPrompts`.
  SFTP operations are serialized with a semaphore in `SftpTab.RunAsync`.
- **Persistence**: JSON via `JsonStore` in `%APPDATA%\Fjord`. Add properties with defaults; never rename existing ones
  without migration (users' profiles.json would lose data).
- **Tunnels**: rules (`TunnelRule`) are stored per session; `TunnelManager` runs them on one connected `SshClient` and
  turns SSH.NET/socket errors into plain-language messages. `TunnelsPanel` sits *beside* the WebView2 terminal (never
  overlapping it). User-facing wording lives in `TunnelRule.Describe/UsageHint` and `TunnelPresets` — keep it non-technical.
- **SFTP tab** is split into partials: `SftpTab.xaml.cs` (navigation, transfers, plumbing), `.Editing.cs` (open → watch
  → upload on save, with server-change check), `.MoveCopy.cs` (cut/copy/paste, `cp -a` over SSH with SFTP streaming
  fallback), `.DragDrop.cs` (drag-out to Explorer as virtual files), `.TwoPane.cs` (optional `LocalPane` on the left, toggled live by `AppSettings.SftpTwoPane` via `AppSettings.Saved`). Drag-out uses `VirtualFileDataObject`
  (raw COM: FileGroupDescriptorW + FileContents streams + IDataObjectAsyncCapability) started with `OleDrag.Run`, not
  WPF's DataObject — an earlier version that handed Explorer temp CF_HDROP paths and downloaded inside GetData crashed
  explorer.exe. Drags inside the app are recognised via the static `s_activeDrag`, not a data format. Server-side logic lives
  in `Fjord.Core/Sftp/RemoteFileOps.cs`.
- **Permissions**: use `UnixMode.Apply` (SetAttributes). SSH.NET's `ChangePermissions(short)` takes octal digits as a
  decimal number (755, not 0x1ED) and clears setuid/setgid/sticky.
- **Colour schemes** live in `Services/ColorSchemes.cs` (one line per scheme); `ColorSchemes.For(profile)` resolves the
  session override or the Settings default. Terminals re-apply on `AppSettings.Saved` / `ProfileStore.Changed`.
  **Colour prompt** (`Core/Ssh/ColorPromptInstaller.cs`) edits the server's rc file inside `# >>> Fjord colour prompt >>>`
  markers; tested on Debian bash (root + login-file fallback), zsh, Alpine BusyBox ash, fish (refused). Every
  command it runs has a timeout, because start-up files can wait for input.
- Help never blocks work: guides open in the non-modal `GuideWindow` (`Assets/guide/*.html`); inline hints over popups.
- Icons are Segoe Fluent Icons code points written as `""` escapes (not raw characters).

## Verified behaviour

- `OpenSshKeyGenerator` output (Ed25519 and RSA-4096, with and without passphrase) was checked with OpenSSH 9.6:
  `ssh-keygen -y` derives the same public key and `ssh-keygen -Y sign/verify` works with the private key.
  If you change the key code, re-run that check.
- `BcryptPbkdf.cs` is vendored from SSH.NET (ISC licence) — don't modify.
