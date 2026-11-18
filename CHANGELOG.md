# vTerm changelog

## 0.2.3

- The drop-down hides again when you click elsewhere (the desktop, another window, a vShell bar) after opening it with the hotkey or clicking in its text.
- Pressing the hotkey while the drop-down is open hides it.

## 0.2.2 · pre-release

- Updating no longer takes 20 seconds to restart vTerm after the drop-down has been used: the drop-down closes with everything else.

## 0.2.1 · pre-release

- Selecting with the mouse shows the highlight as you drag (it only appeared once the shell wrote something).

## 0.2.0 · pre-release

- vTerm2 (a preview): the terminal is now libghostty-vt (Ghostty's terminal engine), drawn by vTerm itself - xterm.js and WebView2 are gone.
- About half the memory: no browser processes (a vTerm with one tab: about 130 MB, was 280 MB; five busy tabs: about 175 MB, was 420 MB).
- The text is drawn in vTerm's own window, so the drop-down's text slides with it and the window's edges always resize.
- A tab that moves to another window keeps its whole screen and scrollback exactly as they were.
- Ctrl+Shift+Home / End jump to the top / bottom of the scrollback; Shift+PageUp / PageDown scroll it a page.
- Ctrl+click opens a web address shown in the terminal.
- Emoji are drawn in one colour for now.

## 0.1.10 · release

- A settings.json that can't be read no longer loses your saved connections: it is kept (settings.broken-…json), the save before it is used, and vShell's old list is never brought over it.
- The file panel works on the server its rows came from: switching tabs while it loads can't send a Delete, Rename or upload to the other server, and an upload never lands in the server's root.
- A saved password is only given to the server it is for (not a jump host), a key's passphrase is never sent as a password, and a wrong saved password is tried once, not three times.
- Copy my key to this server can't join the new key onto the last line of authorized_keys.
- A command that floods the screen (cat of a huge file) no longer makes memory climb or Ctrl+C seem dead: output is passed on a piece at a time and the shell waits its turn.
- vTerm starts about a second and a half sooner when it isn't running yet (vSuite.Core 1.0.41).

## 0.1.9 · release

- Open in vTerm (a folder's right-click menu) opens the new tab in that folder.

## 0.1.8 · release
- Behind the scenes: automated tests, test hooks (a test can drive vTerm without the mouse or keyboard), and every build uses the newest shared library.
- Behind the scenes: saved passwords, the small forms and the update notice's window are the shared library's (the same code as vPad, vView and vShell).

## 0.1.7 · pre-release
- The update notice's progress bar follows the download, instead of standing still until the end (vSuite.Core 1.0.24).
- Options: the scrollbar is the new one too, and keeps clear of the window's resize edge.

## 0.1.6
- The terminal's own scrollbar matches the other apps': never a tiny thumb in a long scrollback, and it widens when you point at it or drag it.

## 0.1.5
- Scrollbars easier to grab in the side panel and Options - never a tiny thumb, wider when you point at them (vSuite.Core 1.0.23).

## 0.1.4 · release
- Tabs look and work like vXplorer's: they share the row (arrows to scroll through them when they don't fit, instead of the All tabs menu), a dragged tab slides the others out of its way, and one pulled out follows the pointer.
- Dragging a window's only tab moves the window; let go on another vTerm window's tabs and it joins them.

## 0.1.3

- Update from vShell's Settings opens a vTerm window, so you can see it updating (it updated out of sight while only the drop-down was in use)

## 0.1.2

- No changes - a new version number for trying updates (vShell's Settings > Updates > vSuite apps)

## 0.1.1

- Windows can be resized from their left, right and bottom edges (the text area was covering them)
- Drag a tab out to make a new window, onto another vTerm window to move it there, or along the tabs to reorder
- The drop-down's only tab can move into a window of its own (its menu, Ctrl+Shift+O or dragging it out)

## 0.1.0

- vTerm: vShell's terminal as an app of its own - PowerShell, cmd, WSL and SSH in tabs, in your vShell theme
- Saved SSH connections, quick connect, import from ~/.ssh/config, your key and copying it to a server
- Files panel (SFTP) for the SSH tab in front: browse, upload (or drop files), download, open, rename, delete
- Tabs move between windows with their shells running
- The drop-down terminal for vShell's bars: slides out of the bar, hides when you click away, its shells keep running
- The first start brings over vShell's terminal settings and saved connections (passwords included)
- Starting vTerm again opens a new window; `--profile`, `--dir`, `--run`, `--ssh` and `--host` open a tab in the running vTerm
- vTerm's icon at the top left opens its menu (new tab / window, Options, updates, About), as in vXplorer and vView
- Options: new-tab profile, profiles, font, text size, colours, opacity, keep on top, updates
- Options > Connections: your saved connections - add, edit, delete, or add the hosts in ~/.ssh/config
- Updates from the vSuite site; an installer with "Open in vTerm" on folders
