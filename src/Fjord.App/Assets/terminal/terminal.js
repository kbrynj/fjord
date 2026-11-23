// Bridge between xterm.js and the C# host (TerminalTab.xaml.cs).
// Host -> page:  { t: 'data', b: <base64 bytes> } | { t: 'focus' } | { t: 'status', text } | { t: 'options', ... }
// Page -> host:  { t: 'ready'|'resize', cols, rows } | { t: 'input', d } | { t: 'binary', d }
//                { t: 'copy', text } | { t: 'paste' } | { t: 'open', uri } | { t: 'hotkey', k }
(() => {
  const host = window.chrome.webview;
  const post = (msg) => host.postMessage(JSON.stringify(msg));

  // Settings arrive in the URL (?cfg=base64 JSON) so the terminal is configured before first paint.
  let cfg = {};
  try { cfg = JSON.parse(atob(new URLSearchParams(location.search).get('cfg') || '') || '{}'); } catch { }

  const theme = {
    background: '#0c0c0c', foreground: '#cccccc', cursor: '#ffffff', selectionBackground: '#264f78',
    black: '#0c0c0c', red: '#c50f1f', green: '#13a10e', yellow: '#c19c00', blue: '#0037da', magenta: '#881798',
    cyan: '#3a96dd', white: '#cccccc', brightBlack: '#767676', brightRed: '#e74856', brightGreen: '#16c60c',
    brightYellow: '#f9f1a5', brightBlue: '#3b78ff', brightMagenta: '#b4009e', brightCyan: '#61d6d6', brightWhite: '#f2f2f2',
  };

  const term = new Terminal({
    fontFamily: cfg.fontFamily || "'Cascadia Mono', Consolas, monospace",
    fontSize: cfg.fontSize || 14,
    scrollback: cfg.scrollback || 10000,
    cursorBlink: cfg.cursorBlink ?? true,
    cursorStyle: 'block',
    theme,
    allowProposedApi: true,
    macOptionIsMeta: false,
    rightClickSelectsWord: false,
  });
  window.term = term;

  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new WebLinksAddon.WebLinksAddon((e, uri) => {
    if (e.ctrlKey) post({ t: 'open', uri });   // Ctrl+click opens links, like Windows Terminal
  }));
  term.open(document.getElementById('term'));

  const doFit = () => { try { fit.fit(); } catch { } };
  doFit();
  new ResizeObserver(doFit).observe(document.getElementById('term'));

  term.onResize(({ cols, rows }) => post({ t: 'resize', cols, rows }));
  term.onData((d) => post({ t: 'input', d }));
  term.onBinary((d) => post({ t: 'binary', d }));
  term.onTitleChange((title) => post({ t: 'title', title }));

  // PuTTY-style clipboard: selecting copies, right-click pastes.
  const copySelection = () => {
    const text = term.getSelection();
    if (text) post({ t: 'copy', text });
    return !!text;
  };
  // Copy once the mouse is released (not on every pixel of the drag).
  document.addEventListener('mouseup', (e) => {
    if (e.button === 0 && (cfg.copyOnSelect ?? true) && term.hasSelection()) copySelection();
  });

  document.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (!(cfg.copyOnSelect ?? true) && term.hasSelection()) { copySelection(); term.clearSelection(); }
    else post({ t: 'paste' });
  });

  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') return true;
    const k = e.key.toLowerCase();
    if (e.ctrlKey && e.shiftKey && k === 'c') { copySelection(); return false; }
    if (e.ctrlKey && e.shiftKey && k === 'v') { post({ t: 'paste' }); return false; }
    if (e.shiftKey && k === 'insert') { post({ t: 'paste' }); return false; }
    if (e.ctrlKey && k === 'tab') { post({ t: 'hotkey', k: e.shiftKey ? 'prevtab' : 'nexttab' }); return false; }
    if (e.ctrlKey && e.shiftKey && k === 't') { post({ t: 'hotkey', k: 'duplicate' }); return false; }
    if (e.ctrlKey && e.shiftKey && k === 'w') { post({ t: 'hotkey', k: 'close' }); return false; }
    if (e.ctrlKey && (k === '=' || k === '+')) { setFontSize(term.options.fontSize + 1); return false; }
    if (e.ctrlKey && k === '-') { setFontSize(term.options.fontSize - 1); return false; }
    if (e.ctrlKey && k === '0') { setFontSize(cfg.fontSize || 14); return false; }
    return true;
  });

  function setFontSize(size) {
    term.options.fontSize = Math.max(6, Math.min(40, size));
    doFit();
  }

  function decode(b64) {
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes;
  }

  host.addEventListener('message', (e) => {
    const m = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
    switch (m.t) {
      case 'data': term.write(decode(m.b)); break;
      case 'status': term.write('\r\n\x1b[90m' + m.text + '\x1b[0m\r\n'); break;
      case 'focus': term.focus(); break;
      case 'clear': term.reset(); break;
      case 'options':
        if (m.fontFamily) term.options.fontFamily = m.fontFamily;
        if (m.fontSize) term.options.fontSize = m.fontSize;
        doFit();
        break;
    }
  });

  term.focus();
  post({ t: 'ready', cols: term.cols, rows: term.rows });
})();
