# libghostty-vt bench

A test bench, not part of vTerm (vTerm.csproj leaves `bench\` out). It compares vTerm's display as it is (xterm.js in WebView2) with
[libghostty-vt](https://lib.ghostty.org/vt/) (Ghostty's terminal engine as a C library, MIT) plus a small WPF renderer, on the same output.

## Run it

```
powershell -ExecutionPolicy Bypass -File bench\get-ghostty.ps1      # libghostty-vt 1.0.1's Windows x64 DLL into bench\native (checked against SHA256SUMS)
dotnet build bench\GhosttyBench.csproj -c Release
bench\bin\Release\net10.0-windows\GhosttyBench.exe --mb 16          # --fresh = a new WebView2 profile (a first-ever start), --quick = one run each
```

It opens a window for each terminal in turn (leave them uncovered: Chromium throttles hidden windows) and writes
`bench\results\results-*.md` plus screenshots of both renderers (to check the drawing).

## What it measures

- **Start-up**: to a terminal on screen. libghostty-vt: DLL load, terminal, renderer, first frame. xterm.js: WebView2 environment,
  page, first terminal.
- **Parsing alone**: libghostty-vt's `ghostty_terminal_vt_write` in 16 KB pieces; xterm.js's `write` on a terminal off the page (it
  stops drawing what it can't see).
- **Parsing + drawing**: libghostty-vt fed by a reader thread with the screen drawn every display frame; xterm.js written inside the
  page as fast as it takes it.
- **vTerm's real path**: xterm.js fed as `TerminalView.FlushOutput` does (StringBuilder, 128K characters posted as JSON every 15 ms
  Background tick, the 2M-character hold-back).
- **Through ConPTY**: `cmd /c type` of the workload in vTerm's own `ConPty` class (`src\Core\ConPty.cs`, linked in) - ConPTY alone,
  into libghostty-vt, and into xterm.js by vTerm's path.
- **Memory**: one idle terminal, and 10 terminals with ~10,000 lines of scrollback each (process private bytes; for WebView2 every
  one of its processes).

## The renderer (`GhosttyView.cs`)

One DrawingVisual per row, only rows libghostty-vt marks dirty are rebuilt; background runs as rectangles, text as GlyphRuns
(Cascadia Mono, falling back per character to a few installed fonts); a block cursor. It has no selection, links, IME, ligatures,
font shaping, blinking, scrollback scrolling or input - what a real one would need before it could replace xterm.js.

## Workloads (`Workloads.cs`)

Made from a fixed seed: plain log lines, colour-heavy lines (16 / 256 / true colour), Unicode (accents, CJK, emoji, box drawing), and a
full-screen app redrawing itself (cursor moves + colours, alternate screen).
