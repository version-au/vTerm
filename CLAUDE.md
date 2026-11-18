# vTerm - notes for Claude Code

vTerm is a Windows 11 terminal (C# / .NET 10, WPF) in vShell's look, part of V's suite with vShell, vXplorer and vView: PowerShell,
cmd, WSL and SSH in tabs (ConPTY + libghostty-vt, drawn by vTerm), saved SSH connections with a side panel and SFTP files. It was vShell's
Terminal module, moved out into an app of its own (2026-11-07). Owner: V (Steven).

**vTerm2 (branch `vterm2`, 0.2.x, V 2026-11-18):** xterm.js in WebView2 replaced by libghostty-vt (Ghostty's terminal engine, a C
library, MIT - `lib\libghostty-vt\`, its README says where the DLL came from) and vTerm's own WPF renderer. Shipped as pre-releases under
the same name / settings; if V isn't happy, go back to tag `vterm-xterm-0.1.10` (the last xterm.js vTerm) - with a higher version
number, the updater never goes down. Merge into `main` only when V says so. `bench\` (on main) is the test bench that compared them.
Repo: https://github.com/version-au/vTerm (branch `main`, GPL-3.0). This machine's notes (working folder, remotes): `CLAUDE.local.md` (not in git).

## The plan (V, 2026-11-07)

1. **vTerm as a standalone app** (this repo, 0.1.x) - done first, tested on its own.
2. Then the **drop-down from vShell's bars**: vTerm keeps running in the background; vShell's Terminal module (bars only - the widget
   terminal goes) asks vTerm over a named pipe to drop down at the bar's edge and width, in vShell's theme. vTerm owns the window
   and shells, so tabs move between the drop-down and normal windows.
3. vShell's `TermOpen.Host` (plugin API: Komodo, Proxmox, Linux Updates "Open shell") forwards to vTerm (`--host … --name … --user …
   --run …` / the pipe) - the plugins don't change. `TermOpen.Terminal` (returns a TerminalView, unused) keeps its API line and returns null.
4. vShell's built-in terminal (TerminalView, ConPty, WebView2, SSH.NET) is removed. Settings > Updates > vSuite apps lists vTerm.

## The shared library: vSuite.Core

- **The suite's design guide** is `vSuite.Core\docs\design-guide.md` (the window, theme, icon, controls, keys, words, Options, release): follow it for anything new here.
- **The changelog says which builds were which** (vSuite.Core 1.0.28): every build marks its version's heading in CHANGELOG.md " · pre-release" (`build.cmd pre`) or " · release" (a release replaces the pre-release mark) - commit CHANGELOG.md after each build. Write new headings without a mark.

- Theme, glass, styles, Ask, Instance, Log, updater, update notice, Options blocks, AppIcon / WindowIcons come from **vSuite.Core**
  (https://github.com/version-au/vSuite.Core, MIT), the git submodule `vSuite.Core\` - read its CLAUDE.md.
  A fresh clone needs `git submodule update --init`. Program calls `App.Init("vTerm", "VTERM", "https://vsuite.vrsion.com")`.
- A change to a shared piece is made in vSuite.Core (with its tests), pushed there, then `git -C vSuite.Core pull` here and committed
  with vTerm's own version bump.

## Build and run

- `dotnet build vTerm.csproj -c Release` -> `bin\Release\net10.0-windows\vTerm.exe`. Close a running vTerm first.
- "Release build" = `build.cmd` (by full path on a network share - see `CLAUDE.local.md`): `dist\vTerm.exe` (self-contained, one file),
  `dist\setup\vTerm-setup.exe` (Inno Setup, `installer\vTerm.iss`, per user, "Open in vTerm" on folders), `dist\site\vterm\` for the
  update site (vSuite.Core's `site-release.ps1`); `build.cmd upload` puts it on the site (`pre` = a pre-release). Only when V asks.
- Log: `%APPDATA%\vTerm\vterm.log`. Settings: `%APPDATA%\vTerm\settings.json` (`AppSettings`). (`%APPDATA%\vTerm\WebView2` and
  `\terminal` are left over from the xterm.js vTerm - unused, left alone while going back is possible.)
- `ghostty-vt.dll` is copied next to vTerm.exe when built and goes inside it when published as one file (checked: the published exe
  runs it). To update it: `bench\get-ghostty.ps1 -Version <new>` (checks SHA256SUMS), copy `bench\native\ghostty-vt.dll` + LICENSE into
  `lib\libghostty-vt\` and update its README.txt. Its C ABI is stable across 1.x.
- Tests: `VTERM_DATA_DIR` (settings + log) to a scratch folder, `VTERM_VSHELL_DIR` (theme / import source). Stop only the test's own
  process (`taskkill /T /F /PID n` - its shells are child processes). The first start of a scratch folder imports V's real vShell
  terminal settings into it (read only - vShell's file isn't changed).
- `vterm.ico` = `vTerm.exe --make-icon vterm.ico` (the drawing is `src\Ui\Brand.cs`), remade when the drawing changes.

## House rules (as in vShell, vXplorer and vView)

- **Only make a release build when V asks.** Otherwise just make the change and commit it.
- Every user-visible change: bump `<Version>` in `vTerm.csproj` (0.1.N - next N once the current one has been given to V), a section at
  the top of **CHANGELOG.md** (`## 0.1.N`, one short line per change), test steps in **TESTING.md**.
- Commit messages: `0.1.N: what changed` (short, lower-case after the colon), one commit per change set.
- Times shown to the user are 12-hour (`TimeText.Stamp`). UI text is plain, friendly English. Colours are `vx.*` resources (the
  terminal's own drawing uses `TermLook`, worked out from the same Theme).

## Layout

- `src/Program.cs` - entry: askpass mode (`TermSsh.IsAskpass`: ssh's SSH_ASKPASS is vTerm.exe; VTERM_ASKPASS_* variables), `--make-icon`,
  `--update-watchdog`, one copy per settings folder (`Instance.HandOff(StartRequest.ToHandOff(args))` - the running vTerm gets
  "vterm:" + the arguments as JSON), the first-start import from vShell, the first window.
- `src/Core/` - `AppSettings` (terminal, window, updates; `SaveSoon`), `VShellImport` (vShell's settings.json: TermConnections,
  TermProfiles, TermFont / Size / Colors / SeeThrough / PanelOpen, the first Terminal module's TermProfile), `ConPty`, `TermConnections`
  (`TermConnection` - same JSON as vShell's, `TermSsh`), `TermProfiles`, (DPAPI secrets: vSuite.Core's `Secret`, 1.0.27 - "dpapi:" like vShell's; saved passwords
  carry over), `Native`.
- `src/Ui/MainWindow.cs` - a window (WindowChrome, `WindowEffects.Look` glass, IThemedWindow) holding one `TerminalView`; buttons right of
  the tabs (update, menu, keep on top, min / max / close); moves by the strip; tabs move between windows (`NewWindowFor`,
  `OtherWindows`); `Run(StartRequest)`; WM_COPYDATA hand-offs. `StartRequest` - the command line (`--profile --dir --run --title --ssh
  --host --name --user --why --new-window`, a folder), and resolving `--host` to a saved connection (or offering to save one).
- `TerminalView(.Panel).cs`, `TermSftp`, `TermDialogs` - from vShell (see vShell's CLAUDE.md for the history): tabs, sessions, the
  side panel. `TermLook` stands in for vShell's Theme. `FormWindow` (+ `Looks`; vSuite.Core's since 1.0.27) replaces vShell's
  FormWindow / PromptWindow / Questions.
- The terminal (0.2.0): `Core\Ghostty.cs` (`Gt`: the C API's P/Invoke - names / numbers are the headers', bench\native\include after
  get-ghostty.ps1), `Core\TermEngine.cs` (one per tab: the terminal, its render state, key / mouse encoders, selection gestures,
  formatter; every call holds `Lock`; ConPty's `Data` bytes go straight in on the reader thread (`Feed`); the library's answers to the
  shell come out as `Reply` after the lock is let go - never write to the pty inside the callback), `Ui\TermScreen.cs` (the renderer and
  input: one DrawingVisual per row, only dirty rows redrawn, GlyphRuns on a cell grid snapped to device pixels with per-character font
  fallback, frames at Background priority at most every 8 ms so input comes first; keys via `PressKey` (`KeyMap`: GhosttyKey codes and
  ToUnicodeEx text) - text-typing keys come through TextInput; mouse: selection gestures, or the program's mouse when it asked (Shift
  overrides), the wheel, Ctrl+click links; the scrollbar; the IME window at the cursor). A tab's engine moves with it between windows
  (no serialising). Emoji (0.2.4): WPF can't draw colour fonts, so `ColorEmoji` has Windows draw an emoji cell's whole cluster (DirectWrite through Direct2D onto a WIC bitmap, colour fonts on; COM by function pointers, vtable slots in the code) into a cached bitmap; grapheme cluster mode (2027) is on by default, so skin tones / joined emoji / VS16 symbols are one two-cell character. Flags stay letters (Windows' emoji font has none).
- `DropDown` - vShell's drop-down (layered: shadow room, vSuite `Glass`, the card with the text on it; slides with a TranslateTransform),
  placed where the TermRequest says (X / Y / W / H = the card in device pixels, its bar side against the bar). `Handle`
  = toggle / show / hide ("shown" / "hidden"); hides on leave (`LeftSoon`), Shift+Esc, Alt+F4.
- `Hub` - settings + vShell's theme for everyone, `Handle(TermRequest)` (the pipe: toggle / show / hide, open-host, args, quit-if-idle;
  anything that may ask a question runs a moment later so the pipe answers at once), `MaybeQuit` (ShutdownMode is explicit: vTerm ends
  when no window is open and the drop-down was never used). Program serves `TermLink.PipeName(App.DataFolder)`; a second start sends
  "args" there first (then the older Instance hand-off); `--background` = started by vShell, no window (ends after a minute unused).
- Tabs (0.1.4): vSuite.Core's `TabStrip` (vXplorer's look, arrows, sliding drag, tear out); `TerminalView.Drag.cs` hears it (Picked
  selects without focus, PressEnded focuses the text, MovedTo = `MoveTo` the other terminal, TornOut = `DropPoint` + `PopOut`;
  `CanTearLast` = `LastTabCanLeave`). Before 0.1.4 - dragging a tab: press = capture + `TabPressed` (FocusTerm waits - the window activating handed the
  keyboard to the text window, which cut the press off), moved 8 px = a drag (`TabGhost` follows the pointer; the button is read with
  GetAsyncKeyState), release = `Drop`: `ViewAt` (WindowFromPoint's root vs every live view's window / text window) - own strip: `Reorder`;
  another view: `MoveTo`; open desktop: `DropPoint` + `NewWindowFor` (MainWindow puts the new window there), or the window's only tab moves
  the window. `LastTabCanLeave` = the drop-down (its only tab can go). (Before 0.2.0 the text was a separate window over the terminal,
  `TermSurface`, with `KeepOffEdges` / `Suspend` / `Track` to work around it - all gone.)
- `OptionsWindow` (OptionsWindowBase: General, Look, Connections, Updates, About), `NoticeWindow` (vSuite.Core's CornerWindow / NoticeWindow since 1.0.27, from vView's - the text window
  would cover a notice drawn in the window), `Brand` (the icon).

## Things learned the hard way

- Windows PowerShell's `Get-Content -Raw` reads UTF-8 files without a BOM as ANSI - it broke the Segoe Fluent glyphs when files were
  copied over from vShell. Use `[IO.File]::ReadAllText(path, [Text.Encoding]::UTF8)`.
- vShell's notes on the terminal still hold: never Measure tabs in ArrangeOverride (TermTabsPanel); menus are placed at the pointer by
  hand (`OpenAtPointer`).
- Tests that start vTerm from a shell tool: give it its own output handles (`Start-Process -RedirectStandardOutput <file>`), or the tool
  waits for vTerm to exit (it holds the tool's pipe).

## Tests

- `dotnet test tests\vTerm.Tests` (xunit) - the parts with no window: the command line and its hand-off, quick SSH addresses, finding a saved connection, profiles as text. Run them after changing `src\Core` or the command line; add tests for new window-less code.
- `VTERM_TEST_HOOKS=1`: a test drives vTerm by WM_COPYDATA (`MainWindow.TestHooks.cs` + `TerminalView.TestHooks.cs` - newtab / tab / close / type (to the shell) / text (typed, through the screen) / key (e.g. `key Ctrl+C`, through the screen's key
  handling) / wheel / selectall / copy / paste / moveout / newwindow / closewindow / options / shot (a PNG) / dump (with the front tab's last lines)), sent with `vSuite.Core\scripts\test-send.ps1 -ProcessId N -Command "…" -Title "- vTerm"`; `VTERM_TEST_ASK=yes|no`. **Use these, not the real mouse or keyboard.**
- **Differs from the design guide on purpose (V, 2026-11-08):** Options' pages are General, Look, Connections, Updates, About - Connections stays after Look (the guide puts an app's own pages before Look). Keep it.
