using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace vTerm.Core
{
    /// <summary>
    /// One tab's terminal: libghostty-vt's terminal (screen, scrollback, modes), the render state the screen is drawn from, and the
    /// encoders for keys, mouse and paste. The shell's output goes in on its reader thread (Feed); the window reads and draws on its own.
    /// Every call into the library holds Lock. Answers the terminal owes the shell (cursor position, device attributes …) come out as
    /// Reply, after the lock is let go.
    /// </summary>
    public sealed unsafe class TermEngine : IDisposable
    {
        public readonly object Lock = new object();
        IntPtr _term, _state, _rows, _cells, _keyEnc, _keyEvt, _mouseEnc, _mouseEvt, _gesture;
        GCHandle _self;
        readonly List<byte[]> _replies = new List<byte[]>();
        bool _titleChanged, _bell;
        int _disposed;

        public int Cols { get; private set; }
        public int Rows { get; private set; }

        /// <summary>Bytes the shell should get (the terminal's answers to its questions). On whichever thread fed the output.</summary>
        public event Action<byte[]> Reply;
        /// <summary>The program set the title (OSC 0 / 2). On whichever thread fed the output.</summary>
        public event Action<string> TitleChanged;
        /// <summary>Something changed on screen. On whichever thread fed the output.</summary>
        public event Action Changed;
        public event Action Bell;

        public IntPtr Terminal { get { return _term; } }
        public IntPtr State { get { return _state; } }
        public IntPtr RowIterator { get { return _rows; } }
        public IntPtr RowCells { get { return _cells; } }

        public TermEngine(int cols, int rows, int scrollbackLines)
        {
            Cols = Math.Max(2, cols); Rows = Math.Max(1, rows);
            Check(Gt.ghostty_terminal_new(IntPtr.Zero, out _term, (ushort)Cols, (ushort)Rows), "terminal");
            nuint lines = (nuint)Math.Max(100, scrollbackLines);
            Gt.ghostty_terminal_set(_term, Gt.OPT_SCROLLBACK_MAX_LINES, &lines);
            _self = GCHandle.Alloc(this);
            Gt.ghostty_terminal_set(_term, Gt.OPT_USERDATA, (void*)GCHandle.ToIntPtr(_self));
            Gt.ghostty_terminal_set(_term, Gt.OPT_WRITE_PTY, (void*)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, nuint, void>)&OnWritePty);
            Gt.ghostty_terminal_set(_term, Gt.OPT_TITLE_CHANGED, (void*)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnTitle);
            Gt.ghostty_terminal_set(_term, Gt.OPT_BELL, (void*)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnBell);
            Check(Gt.ghostty_render_state_new(IntPtr.Zero, out _state), "render state");
            Check(Gt.ghostty_render_state_row_iterator_new(IntPtr.Zero, out _rows), "rows");
            Check(Gt.ghostty_render_state_row_cells_new(IntPtr.Zero, out _cells), "cells");
            Check(Gt.ghostty_key_encoder_new(IntPtr.Zero, out _keyEnc), "key encoder");
            Check(Gt.ghostty_key_event_new(IntPtr.Zero, out _keyEvt), "key event");
            Check(Gt.ghostty_mouse_encoder_new(IntPtr.Zero, out _mouseEnc), "mouse encoder");
            Check(Gt.ghostty_mouse_event_new(IntPtr.Zero, out _mouseEvt), "mouse event");
            Check(Gt.ghostty_selection_gesture_new(IntPtr.Zero, out _gesture), "selection");
        }

        static void Check(int r, string what) { if (r != Gt.SUCCESS) throw new InvalidOperationException("libghostty-vt " + what + ": " + r); }

        static TermEngine Of(IntPtr userdata) { return userdata == IntPtr.Zero ? null : GCHandle.FromIntPtr(userdata).Target as TermEngine; }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnWritePty(IntPtr term, IntPtr userdata, byte* data, nuint len)
        {
            try
            {
                TermEngine e = Of(userdata);
                if (e == null || len == 0) return;
                var b = new byte[(int)len];
                Marshal.Copy((IntPtr)data, b, 0, b.Length);
                e._replies.Add(b);      // the lock is held: Feed hands them on after letting go
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnTitle(IntPtr term, IntPtr userdata) { try { TermEngine e = Of(userdata); if (e != null) e._titleChanged = true; } catch { } }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnBell(IntPtr term, IntPtr userdata) { try { TermEngine e = Of(userdata); if (e != null) e._bell = true; } catch { } }

        // =================================================================
        // Output in
        // =================================================================
        /// <summary>The shell's output (any thread).</summary>
        public void Feed(byte[] data, int count)
        {
            if (count <= 0 || _disposed != 0) return;
            List<byte[]> replies = null;
            string title = null;
            bool bell;
            lock (Lock)
            {
                if (_term == IntPtr.Zero) return;
                fixed (byte* p = data) Gt.ghostty_terminal_vt_write(_term, p, (nuint)count);
                if (_replies.Count > 0) { replies = new List<byte[]>(_replies); _replies.Clear(); }
                if (_titleChanged) { _titleChanged = false; title = ReadTitle(); }
                bell = _bell; _bell = false;
            }
            if (replies != null && Reply != null) foreach (byte[] r in replies) Reply(r);
            if (title != null && TitleChanged != null) TitleChanged(title);
            if (bell && Bell != null) Bell();
            if (Changed != null) Changed();
        }

        /// <summary>Text vTerm writes itself ("[ended]" lines, the reason a shell couldn't start).</summary>
        public void Feed(string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            Feed(b, b.Length);
        }

        string ReadTitle()
        {
            var s = new Gt.GString();
            if (Gt.ghostty_terminal_get(_term, Gt.DATA_TITLE, &s) != Gt.SUCCESS || s.Len == 0) return "";
            return Encoding.UTF8.GetString(s.Ptr, (int)s.Len);
        }

        // =================================================================
        // Size, colours, scrolling
        // =================================================================
        public void Resize(int cols, int rows, int cellWpx, int cellHpx)
        {
            cols = Math.Max(2, Math.Min(500, cols)); rows = Math.Max(1, Math.Min(300, rows));
            lock (Lock)
            {
                if (_term == IntPtr.Zero) return;
                Gt.ghostty_terminal_resize(_term, (ushort)cols, (ushort)rows, (uint)Math.Max(1, cellWpx), (uint)Math.Max(1, cellHpx));
                Cols = cols; Rows = rows;
            }
            if (Changed != null) Changed();
        }

        /// <summary>The default text, background and cursor colours, and the 16 ANSI colours (the rest of the 256 stay the standard ones).</summary>
        public void SetColors(Gt.Rgb fg, Gt.Rgb bg, Gt.Rgb cursor, Gt.Rgb[] ansi16)
        {
            var pal = new Gt.Rgb[256];
            lock (Lock)
            {
                if (_term == IntPtr.Zero) return;
                fixed (Gt.Rgb* p = pal)
                {
                    Gt.ghostty_color_palette_default(p);
                    for (int i = 0; i < 16 && ansi16 != null && i < ansi16.Length; i++) p[i] = ansi16[i];
                    Gt.ghostty_terminal_set(_term, Gt.OPT_COLOR_PALETTE, p);
                }
                Gt.ghostty_terminal_set(_term, Gt.OPT_COLOR_FOREGROUND, &fg);
                Gt.ghostty_terminal_set(_term, Gt.OPT_COLOR_BACKGROUND, &bg);
                Gt.ghostty_terminal_set(_term, Gt.OPT_COLOR_CURSOR, &cursor);
            }
            if (Changed != null) Changed();
        }

        /// <summary>Scroll the view by rows (up is negative).</summary>
        public void ScrollBy(int rows)
        {
            lock (Lock) { if (_term != IntPtr.Zero) Gt.ghostty_terminal_scroll_viewport(_term, new Gt.ScrollViewport { Tag = Gt.SCROLL_DELTA, Value = rows }); }
            if (Changed != null) Changed();
        }

        public void ScrollToRow(long row)
        {
            lock (Lock) { if (_term != IntPtr.Zero) Gt.ghostty_terminal_scroll_viewport(_term, new Gt.ScrollViewport { Tag = Gt.SCROLL_ROW, Value = Math.Max(0, row) }); }
            if (Changed != null) Changed();
        }

        public void ScrollToBottom()
        {
            lock (Lock) { if (_term != IntPtr.Zero) Gt.ghostty_terminal_scroll_viewport(_term, new Gt.ScrollViewport { Tag = Gt.SCROLL_BOTTOM }); }
            if (Changed != null) Changed();
        }

        /// <summary>The scrollback and where the view is in it (rows).</summary>
        public Gt.Scrollbar Scrollbar()
        {
            var sb = new Gt.Scrollbar();
            lock (Lock) { if (_term != IntPtr.Zero) Gt.ghostty_terminal_get(_term, Gt.DATA_SCROLLBAR, &sb); }
            return sb;
        }

        /// <summary>The full-screen programs' screen (vim, htop, less …) is up: the wheel goes to them, not the scrollback.</summary>
        public bool AltScreen
        {
            get
            {
                int screen = 0;
                lock (Lock) { if (_term != IntPtr.Zero) Gt.ghostty_terminal_get(_term, Gt.DATA_ACTIVE_SCREEN, &screen); }
                return screen == 1;
            }
        }

        public bool ModeOn(ushort mode) { lock (Lock) return _term != IntPtr.Zero && Gt.Mode(_term, mode); }

        // =================================================================
        // Keys, mouse, paste, focus
        // =================================================================
        readonly byte[] _out = new byte[256];

        /// <summary>A key as the program in the terminal wants it (its modes: cursor keys, kitty keyboard …); empty = nothing to send.</summary>
        public byte[] EncodeKey(int key, int action, ushort mods, ushort consumed, string text, uint unshifted)
        {
            lock (Lock)
            {
                if (_term == IntPtr.Zero) return new byte[0];
                Gt.ghostty_key_encoder_set_from_terminal(_keyEnc, _term);
                Gt.ghostty_key_event_set_action(_keyEvt, action);
                Gt.ghostty_key_event_set_key(_keyEvt, key);
                Gt.ghostty_key_event_set_mods(_keyEvt, mods);
                Gt.ghostty_key_event_set_consumed_mods(_keyEvt, consumed);
                Gt.ghostty_key_event_set_unshifted_codepoint(_keyEvt, unshifted);
                byte[] utf8 = string.IsNullOrEmpty(text) ? new byte[0] : Encoding.UTF8.GetBytes(text);
                fixed (byte* u = utf8)
                fixed (byte* o = _out)
                {
                    Gt.ghostty_key_event_set_utf8(_keyEvt, new Gt.GString { Ptr = u, Len = (nuint)utf8.Length });
                    nuint n = 0;
                    int r = Gt.ghostty_key_encoder_encode(_keyEnc, _keyEvt, o, (nuint)_out.Length, &n);
                    Gt.ghostty_key_event_set_utf8(_keyEvt, new Gt.GString());
                    if (r != Gt.SUCCESS || n == 0) return new byte[0];
                    var b = new byte[(int)n];
                    Array.Copy(_out, b, b.Length);
                    return b;
                }
            }
        }

        /// <summary>The program in the terminal asked for the mouse (htop, vim, tmux …).</summary>
        public bool MouseTracking
        {
            get
            {
                bool on = false;
                lock (Lock) { if (_term != IntPtr.Zero) Gt.ghostty_terminal_get(_term, Gt.DATA_MOUSE_TRACKING, &on); }
                return on;
            }
        }

        /// <summary>A mouse event for the program (positions in device pixels from the text's top-left); empty = nothing to send.</summary>
        public byte[] EncodeMouse(int action, int button, ushort mods, float x, float y, Gt.MouseEncoderSize size, bool anyPressed)
        {
            lock (Lock)
            {
                if (_term == IntPtr.Zero) return new byte[0];
                Gt.ghostty_mouse_encoder_set_from_terminal(_mouseEnc, _term);
                size.Size = (nuint)sizeof(Gt.MouseEncoderSize);
                Gt.ghostty_mouse_encoder_set(_mouseEnc, Gt.MOUSE_ENC_OPT_SIZE, &size);
                bool pressed = anyPressed;
                Gt.ghostty_mouse_encoder_set(_mouseEnc, Gt.MOUSE_ENC_OPT_ANY_BUTTON_PRESSED, &pressed);
                bool track = true;
                Gt.ghostty_mouse_encoder_set(_mouseEnc, Gt.MOUSE_ENC_OPT_TRACK_LAST_CELL, &track);
                Gt.ghostty_mouse_event_set_action(_mouseEvt, action);
                if (button > 0) Gt.ghostty_mouse_event_set_button(_mouseEvt, button); else Gt.ghostty_mouse_event_clear_button(_mouseEvt);
                Gt.ghostty_mouse_event_set_mods(_mouseEvt, mods);
                Gt.ghostty_mouse_event_set_position(_mouseEvt, new Gt.MousePosition { X = x, Y = y });
                fixed (byte* o = _out)
                {
                    nuint n = 0;
                    if (Gt.ghostty_mouse_encoder_encode(_mouseEnc, _mouseEvt, o, (nuint)_out.Length, &n) != Gt.SUCCESS || n == 0) return new byte[0];
                    var b = new byte[(int)n];
                    Array.Copy(_out, b, b.Length);
                    return b;
                }
            }
        }

        /// <summary>Pasted text as the shell should get it (bracketed when the program asked for that).</summary>
        public byte[] EncodePaste(string text, out bool safe)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? "");
            safe = true;
            if (data.Length == 0) return data;
            bool bracketed = ModeOn(Gt.MODE_BRACKETED_PASTE);
            fixed (byte* d = data) safe = Gt.ghostty_paste_is_safe(d, (nuint)data.Length);
            var buf = new byte[data.Length + 64];
            fixed (byte* d = data)
            fixed (byte* o = buf)
            {
                nuint n = 0;
                int r = Gt.ghostty_paste_encode(d, (nuint)data.Length, bracketed, o, (nuint)buf.Length, &n);
                if (r != Gt.SUCCESS) return data;
                var b = new byte[(int)n];
                Array.Copy(buf, b, b.Length);
                return b;
            }
        }

        /// <summary>Focus in / out, for programs that asked to know (mode 1004); empty otherwise.</summary>
        public byte[] EncodeFocus(bool gained)
        {
            if (!ModeOn(Gt.MODE_FOCUS_EVENT)) return new byte[0];
            fixed (byte* o = _out)
            {
                nuint n = 0;
                if (Gt.ghostty_focus_encode(gained ? Gt.FOCUS_GAINED : Gt.FOCUS_LOST, o, (nuint)_out.Length, &n) != Gt.SUCCESS) return new byte[0];
                var b = new byte[(int)n];
                Array.Copy(_out, b, b.Length);
                return b;
            }
        }

        // =================================================================
        // Selection
        // =================================================================
        /// <summary>A press, drag or release at a cell of the view (x, y = the pointer in device pixels, for word / line clicks and drags);
        /// the selection that makes is shown (none = cleared). True when something is selected.</summary>
        public bool Gesture(int type, int col, int row, double x, double y, Gt.GestureGeometry geo, ulong timeNs)
        {
            bool selected = GestureLocked(type, col, row, x, y, geo, timeNs);
            // the selection (or its clearing) shows now - the screen draws when told something changed
            if (Changed != null) Changed();
            return selected;
        }

        bool GestureLocked(int type, int col, int row, double x, double y, Gt.GestureGeometry geo, ulong timeNs)
        {
            lock (Lock)
            {
                if (_term == IntPtr.Zero) return false;
                IntPtr ev;
                if (Gt.ghostty_selection_gesture_event_new(IntPtr.Zero, out ev, type) != Gt.SUCCESS) return false;
                try
                {
                    var gr = new Gt.GridRef();
                    var pt = new Gt.Point { Tag = Gt.POINT_VIEWPORT, Coord = new Gt.PointCoordinate { X = (ushort)Math.Max(0, Math.Min(Cols - 1, col)), Y = (uint)Math.Max(0, Math.Min(Rows - 1, row)) } };
                    bool haveRef = Gt.ghostty_terminal_grid_ref(_term, pt, &gr) == Gt.SUCCESS;
                    if (haveRef) Gt.ghostty_selection_gesture_event_set(ev, Gt.GEV_REF, &gr);
                    var pos = new Gt.SurfacePosition { X = x, Y = y };
                    if (type != Gt.GESTURE_RELEASE) Gt.ghostty_selection_gesture_event_set(ev, Gt.GEV_POSITION, &pos);
                    if (type == Gt.GESTURE_PRESS && timeNs != 0) Gt.ghostty_selection_gesture_event_set(ev, Gt.GEV_TIME_NS, &timeNs);
                    if (type == Gt.GESTURE_DRAG || type == Gt.GESTURE_AUTOSCROLL_TICK) Gt.ghostty_selection_gesture_event_set(ev, Gt.GEV_GEOMETRY, &geo);
                    if (type == Gt.GESTURE_AUTOSCROLL_TICK)
                    {
                        var vp = new Gt.PointCoordinate { X = (ushort)Math.Max(0, Math.Min(Cols - 1, col)), Y = (uint)Math.Max(0, Math.Min(Rows - 1, row)) };
                        Gt.ghostty_selection_gesture_event_set(ev, Gt.GEV_VIEWPORT, &vp);
                    }
                    if ((type == Gt.GESTURE_PRESS || type == Gt.GESTURE_DRAG) && !haveRef) return HasSelectionLocked();
                    var sel = new Gt.Selection();
                    int r = Gt.ghostty_selection_gesture_event(_gesture, _term, ev, &sel);
                    if (type == Gt.GESTURE_RELEASE) return HasSelectionLocked();
                    if (r == Gt.SUCCESS) { Gt.ghostty_terminal_set(_term, Gt.OPT_SELECTION, &sel); return true; }
                    if (type == Gt.GESTURE_PRESS && r == Gt.NO_VALUE) Gt.ghostty_terminal_set(_term, Gt.OPT_SELECTION, null);
                    return HasSelectionLocked();
                }
                finally { Gt.ghostty_selection_gesture_event_free(ev); }
            }
        }

        /// <summary>Which way a drag past the top / bottom wants to scroll: -1 up, 1 down, 0 none.</summary>
        public int AutoscrollDirection()
        {
            int d = 0;
            lock (Lock) { if (_term != IntPtr.Zero) Gt.ghostty_selection_gesture_get(_gesture, _term, Gt.GESTURE_DATA_AUTOSCROLL, &d); }
            return d == 1 ? -1 : d == 2 ? 1 : 0;
        }

        bool HasSelectionLocked()
        {
            var sel = new Gt.Selection();
            return Gt.ghostty_terminal_get(_term, Gt.DATA_SELECTION, &sel) == Gt.SUCCESS;
        }

        public bool HasSelection { get { lock (Lock) return _term != IntPtr.Zero && HasSelectionLocked(); } }

        public void ClearSelection()
        {
            lock (Lock) { if (_term != IntPtr.Zero) { Gt.ghostty_terminal_set(_term, Gt.OPT_SELECTION, null); Gt.ghostty_selection_gesture_reset(_gesture, _term); } }
            if (Changed != null) Changed();
        }

        public void SelectAll()
        {
            lock (Lock)
            {
                if (_term == IntPtr.Zero) return;
                var sel = new Gt.Selection();
                if (Gt.ghostty_terminal_select_all(_term, &sel) == Gt.SUCCESS) Gt.ghostty_terminal_set(_term, Gt.OPT_SELECTION, &sel);
            }
            if (Changed != null) Changed();
        }

        /// <summary>The selected text as Ghostty copies it (wrapped lines joined, trailing spaces trimmed); "" = nothing selected.</summary>
        public string SelectedText()
        {
            lock (Lock)
            {
                if (_term == IntPtr.Zero) return "";
                var sel = new Gt.Selection();
                if (Gt.ghostty_terminal_get(_term, Gt.DATA_SELECTION, &sel) != Gt.SUCCESS) return "";
                return FormatLocked(&sel);
            }
        }

        /// <summary>The whole screen (and scrollback) as plain text - the tests' dump.</summary>
        public string ScreenText()
        {
            lock (Lock) return _term == IntPtr.Zero ? "" : FormatLocked(null);
        }

        string FormatLocked(Gt.Selection* sel)
        {
            IntPtr f;
            if (Gt.ghostty_formatter_terminal_new(IntPtr.Zero, out f, _term) != Gt.SUCCESS) return "";
            try
            {
                int plain = Gt.FMT_PLAIN;
                bool yes = true;
                Gt.ghostty_formatter_set(f, Gt.FMT_OPT_EMIT, &plain);
                Gt.ghostty_formatter_set(f, Gt.FMT_OPT_UNWRAP, &yes);
                Gt.ghostty_formatter_set(f, Gt.FMT_OPT_TRIM, &yes);
                if (sel != null) Gt.ghostty_formatter_set(f, Gt.FMT_OPT_SELECTION, sel);
                byte* p = null; nuint n = 0;
                if (Gt.ghostty_formatter_format_alloc(f, IntPtr.Zero, &p, &n) != Gt.SUCCESS || p == null) return "";
                try { return Encoding.UTF8.GetString(p, (int)n); }
                finally { Gt.ghostty_free(IntPtr.Zero, p, n); }
            }
            finally { Gt.ghostty_formatter_free(f); }
        }

        /// <summary>The library's own count of the memory this terminal holds (bytes).</summary>
        public long MemoryBytes()
        {
            var m = new MemoryUsage { Size = (nuint)sizeof(MemoryUsage) };
            lock (Lock) { if (_term == IntPtr.Zero || Gt.ghostty_terminal_get(_term, Gt.DATA_MEMORY_USAGE, &m) != Gt.SUCCESS) return 0; }
            return (long)(m.PrimaryResident + m.AltResident);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryUsage
        {
            public nuint Size;
            public byte CompressionSupported;
            public ulong PrimaryPages, PrimaryVirtual, PrimaryResident, PrimaryCompressedPages, PrimaryCompressedBytes, PrimaryImage;
            public ulong AltPages, AltVirtual, AltResident, AltCompressedPages, AltCompressedBytes, AltImage;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Lock)
            {
                if (_gesture != IntPtr.Zero) Gt.ghostty_selection_gesture_free(_gesture, _term);
                if (_mouseEvt != IntPtr.Zero) Gt.ghostty_mouse_event_free(_mouseEvt);
                if (_mouseEnc != IntPtr.Zero) Gt.ghostty_mouse_encoder_free(_mouseEnc);
                if (_keyEvt != IntPtr.Zero) Gt.ghostty_key_event_free(_keyEvt);
                if (_keyEnc != IntPtr.Zero) Gt.ghostty_key_encoder_free(_keyEnc);
                if (_cells != IntPtr.Zero) Gt.ghostty_render_state_row_cells_free(_cells);
                if (_rows != IntPtr.Zero) Gt.ghostty_render_state_row_iterator_free(_rows);
                if (_state != IntPtr.Zero) Gt.ghostty_render_state_free(_state);
                if (_term != IntPtr.Zero) Gt.ghostty_terminal_free(_term);
                _gesture = _mouseEvt = _mouseEnc = _keyEvt = _keyEnc = _cells = _rows = _state = _term = IntPtr.Zero;
            }
            if (_self.IsAllocated) _self.Free();
        }
    }
}
