# vTerm - testing

Test with a scratch settings folder so your own vTerm is never touched: `set VTERM_DATA_DIR=<scratch folder>` before starting
`bin\Release\net10.0-windows\vTerm.exe`.

## 0.2.3: the drop-down hides when you leave it
1. Open the drop-down with vShell's hotkey, click the desktop: it hides. Again, click in its text first, then a PowerShell window / a vShell bar: it hides. The same after clicking its tabs.
2. The hotkey again while it's down: it hides (it didn't when Windows hadn't marked it active).
3. A dialog from its side panel (a connection's settings), a question it asks: it stays down. With Hide on leave off in vShell: it stays down.
4. Test hooks: TermLink "toggle", then scratch ddclick.ps1 (posted click, then another window takes the foreground); the dump says "drop-down: hidden".

## 0.2.2: updating with the drop-down used
1. Open the drop-down from a vShell bar (a tab in it), then install an update (Options > Updates, a pre-release): vTerm restarts within a couple of seconds - vterm.log shows no "didn't close within 20 s". Test hook: open the drop-down (TermLink "toggle"), then `updateclose`: the process ends at once ("vTerm ending (nothing open)").
2. Without an update: Alt+F4 in the drop-down still only hides it, its shells keep running.

## 0.2.1: the selection shows while dragging
1. Drag across some text at an idle prompt: the highlight follows the pointer as you drag. Double-click a word, triple-click a line: highlighted at once. A plain click clears it at once. Test hook: `drag 10 9 40 12` then `shot`.

## 0.2.0: vTerm2 - libghostty-vt instead of xterm.js / WebView2
Branch `vterm2`; tag `vterm-xterm-0.1.10` is the last xterm.js vTerm. Test hooks added: `text <t>` (typed, through the screen), `key <Ctrl+Shift+Name>`
(a key through the screen's own handling), `wheel <notches>`, `selectall`, `copy`, `paste`, `shot <file.png>`; `dump` shows the front tab's last lines.
1. Start vTerm: PowerShell's prompt in its colours on the glass, the window's title follows the shell's. No msedgewebview2.exe under vTerm.exe
   (Task Manager > Details, or the dump's process tree).
2. Keys: Up / Down (history), Tab (completion), Ctrl+C (stops a command; copies when something is selected), Ctrl+V / Shift+Insert / right-click
   with nothing selected (paste), Ctrl+Left / Right, Home / End, Alt+letter, F-keys. AltGr characters on a non-English layout. Dead keys (é via ' + e).
   Japanese / Chinese / Korean IME: the composition window sits at the cursor; the text lands in the shell.
3. Selection: drag selects (the accent colour), double-click a word, triple-click a line, drag past the top / bottom scrolls; right-click
   copies it; Ctrl+C copies it. Shift+drag selects in a program that has the mouse (htop, vim).
4. Scrollback: `1..3000 | % { "line $_" }`, the wheel scrolls (3 lines a notch), the scrollbar's thumb shows and widens when pointed at; drag it,
   click above / below it (a page). Shift+PageUp / PageDown, Ctrl+Shift+Home / End. Typing jumps back to the bottom.
5. Full-screen programs: `edit` (Microsoft Edit), vim / htop / less / tmux over SSH or WSL: they draw, take keys and the mouse (clicks,
   wheel); leaving them gives back the screen as it was. Without the mouse (less), the wheel is arrow keys.
6. Colours and styles: `Write-Host` with bold, italic, underline, strikethrough, inverse, 256 and true colour; accents, CJK (two cells
   wide), box drawing. Emoji show, in one colour. Options > Look: font, size, colour scheme, see-through change the open tabs at once.
7. Resize the window from every edge (the text never blocks it) and maximise: the shell gets the new size (`$Host.UI.RawUI.WindowSize`),
   long lines rewrap.
8. Tabs: new tab, Ctrl+Tab, close; drag a tab out into its own window and onto another: its screen and scrollback come along exactly,
   the shell still running; Ctrl+Shift+O.
9. The drop-down from a vShell bar: slides out with its text (nothing left behind), takes the keyboard, Shift+Esc / the hide button / a click
   elsewhere hides it, the shells keep running; shown at another bar it takes that size.
10. SSH: a saved connection opens; `exit` shows "[Disconnected …]", Enter connects again. The files panel still works.
11. Ctrl+click a https:// address printed in the terminal: it opens in the browser (the pointer is a hand while Ctrl is held over it).
12. `cat` / `type` of a big file or `1..200000`: streams steadily, Ctrl+C stops it at once, the window stays responsive, memory level.

## 0.1.10: connections, the file panel, saved passwords, copying a key
1. `dotnet test tests\vTerm.Tests`: passes (`SettingsFileTests`, `AskpassTests`).
2. Close vTerm, cut %APPDATA%\vTerm\settings.json short, start it: the connections are there (from settings.json.bak - the save before), settings.broken-<time>.json holds the broken file, vterm.log says so. With no .bak: no connections, but vShell's old ones are NOT imported over it and the broken file is kept.
3. Files panel on server A; switch to a tab on server B that takes a moment to connect: A's rows go at once ("Connecting to B…"), so nothing of A can be clicked. Right-click a row > Delete: the question names the server the row is on. Open the Delete question on A, switch tabs behind it, answer Yes: it is deleted on A.
4. While the panel says "Connecting…" or "Couldn't open …": Upload, New folder and dropping a file say "Wait for the folder to show first" - nothing is put in the server's root.
5. A connection with a jump host that asks for a password, and a saved password for the target: the jump's prompt isn't answered with the target's password (the sign-in to the jump fails; ssh says so). A Key connection with a saved passphrase whose key the server refuses: no password is sent. A wrong saved password: one "Permission denied", not three.
6. On a server, make ~/.ssh/authorized_keys end without a line ending (`printf "ssh-ed25519 AAA old" > ~/.ssh/authorized_keys`), then Copy my key to this server: the file has two lines and both keys work. Again: nothing is added twice.

## 0.1.10: floods of output, a faster start
1. In a tab: `cat` a file of a few hundred MB (or `yes`): the text streams steadily, vTerm's and WebView2's memory stay level, Ctrl+C stops it within a second, and typing in another tab stays quick. Ordinary output (dir, a build log) is as fast as before. Closing the tab or the shell ending mid-flood: its last lines still come before "[ended]".
2. With no vTerm running, start it (the icon, Open in vTerm, vShell's Terminal button): the window / drop-down is there about 1.5 s sooner than 0.1.9. With vTerm already running: a second start still hands over to it (a new tab / window) at once.

## 0.1.9: Open in vTerm opens in the folder. Explorer or vXplorer: right-click a folder (or the empty part of one) > Open in vTerm: the new tab's prompt is in that folder
   (before: the start profile's own folder). With vTerm already running: a new tab in its window, in that folder.. vTerm.exe --dir "C:\some folder" from a command line: the same. --profile "…" --dir "…": that profile, in that folder.. Test hooks: "dump" now shows each tab's folder ("[in C:\…]").

## 0.1.8: tests and test hooks
1. `dotnet test tests\vTerm.Tests`: passes.
2. Start a built vTerm.exe with VTERM_TEST_HOOKS=1 (and a scratch VTERM_DATA_DIR); `vSuite.Core\scripts\test-send.ps1 -ProcessId <pid> -Command "dump C:\temp\s.txt"`:
   the file lists its windows and tabs. Without the variable the same message does nothing.

## 0.1.7: the update's progress bar (vSuite.Core 1.0.24)
1. Update to a newer release from this version (the running copy downloads it): the notice's bar and percentage move steadily with the
   download, and the window stays responsive during "Checking and installing…". (Updating TO this version still shows the old behaviour -
   it's the version doing the download that matters.)
2. Settings / Options made small enough to scroll: its scrollbar is the thin one that widens when pointed at, with a ~40 px thumb, and sits a
   little in from the window's right edge - grabbing it never starts a resize.

## 0.1.6: the terminal's scrollbar. Run 1..3000 in a tab: the terminal's scrollbar thumb is about 40 px tall (not a sliver); point at it: it widens and brightens, and stays
   so while dragged. (A web page's scrollbar can't widen for the pointer on its empty track - only on the thumb.). After updating from an older vTerm: the new page is used straight away (it's written again whenever it differs, not only by size).

## 0.1.5: scrollbars (vSuite.Core 1.0.23)
1. Many saved connections / a big SFTP folder in the side panel: its scrollbar thumb stays about 40 px tall and widens when pointed at.
   (The terminal's own scrollbar is xterm's - unchanged.)

## 0.1.4: vXplorer's tabs (vSuite.Core 1.0.21 TabStrip)
1. Tabs look like vXplorer's (rounded card, the Main Accent line under the chosen one, close on the chosen tab and under the pointer),
   in Segoe UI whatever the terminal's font. An SSH tab with its own colour has that colour as its line (faint when not chosen).
2. Open many tabs (Ctrl+Shift+T): narrower down to 90 px, then arrows either side; the wheel over the tabs scrolls them (it used to
   switch tabs - Ctrl+Tab still does). No All tabs button any more.
3. Drag a tab along the strip: the others slide aside, it glides into place; the shell keeps running. A click on a tab: the keyboard is
   in its terminal straight away.
4. Pull a tab out: a copy follows the pointer; let go on the desktop: a new window there; on another vTerm window (its tabs or its text)
   or the open drop-down: it moves there. The drop-down's only tab can be pulled out too.
5. A window's only tab: dragging it moves the window; let go on another window's tabs and it joins them, the empty window closes.

## 0.1.3

**Updating from vShell** (Settings > Updates > vSuite apps > vTerm > Update, with a newer vTerm on the site)
1. vTerm running only for the drop-down (no window): Update opens a vTerm window in front, with the update notice in its corner showing
   the download, then vTerm restarts as the new version.
2. vTerm with a window open: that window comes to the front with the notice.
3. vTerm not running: it starts with a window and the notice.
4. Include pre-releases switched in vShell's Settings: no window opens (vTerm's Options > Updates follows).

## 0.1.1

**Resizing**
1. Drag a vTerm window's left, right or bottom edge (and corners): it resizes. The text area keeps a few pixels clear of those edges
   (WebView2's own window would take the mouse there); maximised, it goes right to the edges. The top edge resizes too.

**Dragging tabs** (the shells keep running throughout)
1. Drag a tab a little way: a label with its name follows the pointer. Let go on empty desktop: a new window opens there with that tab
   (the tabs under the pointer); the old window keeps its other tabs.
2. Drag a tab onto another vTerm window (anywhere on it): it moves into that window. The last tab leaving a window closes it.
3. Drag a tab along its own strip: it takes its new place. Let go elsewhere on its own window: nothing changes.
4. A window's only tab dragged onto empty desktop: the window moves there instead.
5. A short click on a tab still just selects it (and the keyboard goes to the text). Esc / losing the mouse mid-drag: nothing moves.

**The drop-down's only tab**
1. Open the drop-down (vShell's Terminal module) with one tab. Right-click it: "Move to a new window" is there; Ctrl+Shift+O does the same;
   so does dragging it out. The tab goes into a new window and the drop-down slides away. Opening the drop-down again: a fresh tab.
2. Drag a tab from a window onto the open drop-down (Hide when clicking another window off): it moves into the drop-down.

## 0.1.0

**First start (import from vShell)**
1. Start vTerm with an empty scratch folder. The log says "vShell's terminal brought over: N of N connections, with its settings".
2. The side panel (button left of the tabs) lists vShell's saved connections in their folders. One with a saved password connects
   without asking for it (askpass from vTerm.exe).
3. Options > Look shows vShell's terminal font, size and colours; Options > General's new-tab profile is the one vShell's Terminal used.
4. Close and start again: no second import (the log doesn't say it again) - connections added in vShell later don't come over.

**The vTerm menu (the icon at the top left)**
1. The icon is in the theme's colours, like vXplorer's and vView's. Click it: New tab, New window, Options, Check for updates,
   About vTerm, Close window - each does what it says (Check for updates opens Options on Updates).

**Options > Connections**
1. Lists every saved connection by folder, with its address and how it signs in.
2. Add a connection…: the form; OK adds it here and in every window's side panel straight away.
3. Edit (pencil): change the name / folder - the list and the side panels follow. Delete asks, then removes it everywhere.
4. Add the hosts in ~/.ssh/config: adds the named hosts not saved yet (folder "SSH config"), says how many.
5. Edit a connection from a side panel while Options is open: the Options list follows.

**Window**
1. The window is in the vShell theme with its glass (Options > Look > Use the theme's opacity off = solid). Change vShell's theme:
   vTerm follows straight away.
2. Drag the empty part of the tab strip to move; double-click it to maximise / restore; resize from the edges. Maximised, the tabs
   and buttons stay on screen.
3. Pin button: kept on top (every window), text included. Close with a shell running asks first (Options > General can turn it off).
4. Close and start again: the window comes back where it was.

**Tabs**
1. + opens the new-tab profile; the arrow beside it lists the profiles and saved connections; Ctrl+Shift+T / W, Ctrl+Tab work in the text.
2. Right-click a tab > Move to a new window (Ctrl+Shift+O): its shell keeps running and its screen comes along. In the new window,
   right-click > Move to window 1 (…): back again. The last tab leaving a window closes it.
3. `exit` in a shell closes its tab; an SSH tab that disconnects stays, Enter reconnects.

**Starting vTerm again (one vTerm at a time)**
1. With vTerm running, start vTerm.exe again: a new window with a new tab.
2. `vTerm.exe --profile "Command Prompt"`: a Command Prompt tab in the window in front.
3. `vTerm.exe --dir C:\Windows`: a tab of the start profile in C:\Windows. `vTerm.exe --run "ping 127.0.0.1" --title Ping`: that command.
4. `vTerm.exe --ssh user@host`: an SSH tab (the saved connection for that host if there is one).
5. `vTerm.exe --host <a saved host>`: connects with it. `--host 10.9.9.9 --name Test`: asks Save one / Connect / Cancel.
   `--host "" --name Unknown`: asks to make a connection named Unknown.

**The drop-down (vSuite's TermLink pipe - from vShell once it uses vTerm; until then by hand)**
1. `vTerm.exe --background`: no window; the log says "starting in the background (for vShell)". Nothing asked within a minute: it ends.
2. A "toggle" request (vShell's Terminal button / hotkey) with the bar's place: the drop-down slides out of the bar's edge, as wide as
   asked, in the theme with the bar's corners, outline, shadow and blur; the keyboard is in it. The same request again: it slides away.
3. Click another window: it hides (with HideOnLeave). Its dialogs (Edit connection, questions) don't hide it. Shift+Esc / the hide
   button hide it. Its shells keep running - toggle again: the same tabs.
4. Right-click a tab > Move to a new window: it hides, the tab is in a new window. In that window, a tab's menu has Move to the drop-down.
5. Close every window: vTerm keeps running once the drop-down has been used (its shells live on). Without the drop-down it ends.
6. With vTerm running in the background, start vTerm.exe: a window opens (handed over on the pipe).

**Options**
1. Text size slider, font, colours scheme: every window's text changes when you let go.
2. Profiles: edit the text, Save the profiles - the new-tab menu shows them; Back to this PC's shells restores.
3. Updates page: Check now works (nothing is on the site until the first release).

**Installer** (only on a release build): installs to %LOCALAPPDATA%\Programs\vTerm; "Open in vTerm" on a folder opens a tab there;
uninstall offers to delete %APPDATA%\vTerm.
