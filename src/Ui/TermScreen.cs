using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// The terminal's text, drawn by vTerm from libghostty-vt's render state (TermEngine), inside vTerm's own window - no browser, no
    /// window of its own. One DrawingVisual per row; only the rows the library marks changed are drawn again. Takes the keys, the mouse
    /// (selection, links, programs that ask for it, the wheel), IME text, and draws a scrollbar. Shows one engine at a time: the tab in front.
    /// </summary>
    public sealed unsafe class TermScreen : FrameworkElement
    {
        readonly VisualCollection _kids;
        readonly DrawingVisual _back = new DrawingVisual(), _cursorV = new DrawingVisual(), _barV = new DrawingVisual();
        readonly List<DrawingVisual> _rowV = new List<DrawingVisual>();
        Cell[][] _lines = new Cell[0][];
        TermEngine _e;

        // the look
        string _fontName = "";
        double _fontSize = 13;
        Face[] _faces = new Face[0];
        double _cellW = 8, _cellH = 16, _baseline = 12, _dip = 1;
        bool _seeThrough = true;
        Color _solid = Color.FromRgb(0x1E, 0x1E, 0x2E);
        Brush _selBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0xFF));
        Brush _thumb = new SolidColorBrush(Color.FromArgb(0x70, 0x80, 0x80, 0x80));
        Color _cursorColor = Color.FromRgb(0x80, 0x80, 0xFF), _defBg = Colors.Black;
        readonly Dictionary<uint, Brush> _brushes = new Dictionary<uint, Brush>();

        /// <summary>Room around the text (as vTerm's xterm.js page had it), and the scrollbar's strip on the right.</summary>
        const double PadL = 10, PadT = 6, PadB = 4, BarW = 12;

        int _cols, _rows;
        int _frameQueued;
        DateTime _lastFrame;
        readonly DispatcherTimer _frameLater, _blink, _autoscroll;
        bool _blinkOn = true, _focused;

        /// <summary>Typed / clicked input for the shell.</summary>
        public event Action<byte[]> Input;
        /// <summary>The text area's size in cells changed (cols, rows, cell width / height in device pixels).</summary>
        public event Action<int, int, int, int> Resized;
        /// <summary>A link was Ctrl+clicked.</summary>
        public event Action<string> LinkOpened;

        public TermScreen()
        {
            _kids = new VisualCollection(this) { _back };
            _kids.Add(_cursorV);
            _kids.Add(_barV);
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;
            Cursor = Cursors.IBeam;
            InputMethod.SetIsInputMethodEnabled(this, true);
            SnapsToDevicePixels = true;
            _frameLater = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(8) };
            _frameLater.Tick += delegate { _frameLater.Stop(); Frame(false); };
            _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _blink.Tick += delegate { _blinkOn = !_blinkOn; DrawCursor(); };
            _autoscroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _autoscroll.Tick += delegate { AutoscrollTick(); };
            SizeChanged += delegate { Fit(); };
            Loaded += delegate { Fit(); };
            IsVisibleChanged += delegate { if (IsVisible) Frame(true); };
            Fonts("", 13);
        }

        protected override int VisualChildrenCount { get { return _kids.Count; } }
        protected override Visual GetVisualChild(int index) { return _kids[index]; }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); Fonts(_fontName, _fontSize); }

        // =================================================================
        // The engine shown
        // =================================================================
        public TermEngine Engine { get { return _e; } }

        /// <summary>Show this tab's terminal (null = none).</summary>
        public void Show(TermEngine e)
        {
            if (_e == e) return;
            if (_e != null) _e.Changed -= OnChanged;
            _e = e;
            _gesturing = false;
            _autoscroll.Stop();
            if (_e != null) _e.Changed += OnChanged;
            _cols = _rows = 0;
            Fit();
            Frame(true);
        }

        void OnChanged()
        {
            if (Interlocked.Exchange(ref _frameQueued, 1) == 1) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                // at most one frame every 8 ms: a flood of output doesn't keep the window busy drawing
                double since = (DateTime.UtcNow - _lastFrame).TotalMilliseconds;
                if (since < 8) { if (!_frameLater.IsEnabled) _frameLater.Start(); return; }
                Frame(false);
            }));
        }

        // =================================================================
        // Look
        // =================================================================
        sealed class Face
        {
            public GlyphTypeface Normal, Bold, Italic, BoldItalic;
        }

        /// <summary>The text's font and size, the background when it isn't see-through, the selection, cursor and scrollbar colours.</summary>
        public void ApplyLook(string font, double size, bool seeThrough, Color solidBg, Color cursor, Color selection, Color thumb)
        {
            _seeThrough = seeThrough;
            _solid = solidBg;
            _cursorColor = cursor;
            _selBrush = Freeze(new SolidColorBrush(selection));
            _thumb = Freeze(new SolidColorBrush(thumb));
            if (font != _fontName || Math.Abs(size - _fontSize) > 0.01) Fonts(font, size);
            DrawBack();
            Frame(true);
        }

        void Fonts(string font, double size)
        {
            _fontName = font ?? "";
            _fontSize = Math.Max(6, size);
            try { _dip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { _dip = 1; }
            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(_fontName)) names.Add(_fontName.Trim());
            names.AddRange(new[] { "Cascadia Mono", "Consolas", "Segoe UI Symbol", "Segoe UI Emoji", "MS Gothic", "Malgun Gothic", "Microsoft YaHei", "Nirmala UI", "Segoe UI" });
            var faces = new List<Face>();
            foreach (string n in names)
            {
                var ff = new FontFamily(n);
                GlyphTypeface g;
                if (!new Typeface(ff, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out g)) continue;
                if (faces.Exists(x => x.Normal.FontUri == g.FontUri)) continue;
                var f = new Face { Normal = g };
                f.Bold = Try(ff, FontStyles.Normal, FontWeights.Bold) ?? g;
                f.Italic = Try(ff, FontStyles.Italic, FontWeights.Normal) ?? g;
                f.BoldItalic = Try(ff, FontStyles.Italic, FontWeights.Bold) ?? f.Bold;
                faces.Add(f);
            }
            _faces = faces.ToArray();
            _faceOf.Clear();
            GlyphTypeface main = _faces[0].Normal;
            ushort zero;
            double adv = main.CharacterToGlyphMap.TryGetValue('0', out zero) ? main.AdvanceWidths[zero] : 0.6;
            // the cell on whole device pixels, so every row and column sits on the pixel grid
            _cellW = Math.Max(1, Math.Round(adv * _fontSize * _dip)) / _dip;
            _cellH = Math.Max(1, Math.Ceiling(main.Height * _fontSize * _dip)) / _dip;
            _baseline = Math.Round(main.Baseline * _fontSize * _dip) / _dip;
            _cols = _rows = 0;
            Fit();
        }

        static GlyphTypeface Try(FontFamily ff, FontStyle st, FontWeight w)
        {
            GlyphTypeface g;
            return new Typeface(ff, st, w, FontStretches.Normal).TryGetGlyphTypeface(out g) ? g : null;
        }

        static Brush Freeze(Brush b) { b.Freeze(); return b; }

        Brush BrushOf(uint argb)
        {
            Brush b;
            if (_brushes.TryGetValue(argb, out b)) return b;
            if (_brushes.Count > 4096) _brushes.Clear();
            b = Freeze(new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)));
            _brushes[argb] = b;
            return b;
        }

        void DrawBack()
        {
            using (DrawingContext dc = _back.RenderOpen())
            {
                // see-through: nothing (the window's glass / colour shows) - but something to hit, so the mouse works everywhere
                dc.DrawRectangle(_seeThrough ? Brushes.Transparent : BrushOf(Pack(_solid)), null, new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)));
            }
        }

        // =================================================================
        // Size
        // =================================================================
        /// <summary>How many cells fit; tells the tab (and its shell) when that changes.</summary>
        void Fit()
        {
            DrawBack();
            if (_e == null || ActualWidth < 4 || ActualHeight < 4) return;
            int cols = Math.Max(2, (int)Math.Floor((ActualWidth - PadL - BarW) / _cellW));
            int rows = Math.Max(1, (int)Math.Floor((ActualHeight - PadT - PadB) / _cellH));
            if (cols == _cols && rows == _rows && _e.Cols == cols && _e.Rows == rows) return;
            _cols = cols; _rows = rows;
            if (Resized != null) Resized(cols, rows, (int)Math.Round(_cellW * _dip), (int)Math.Round(_cellH * _dip));
            Frame(true);
        }

        public int CellWidthPx { get { return (int)Math.Round(_cellW * _dip); } }
        public int CellHeightPx { get { return (int)Math.Round(_cellH * _dip); } }
        /// <summary>The cells that fit now (0 until laid out).</summary>
        public int FitCols { get { return _cols; } }
        public int FitRows { get { return _rows; } }

        // =================================================================
        // Drawing
        // =================================================================
        struct Cell
        {
            public uint Cp;         // the base character (0 = blank / the tail of a wide one)
            public int Face;
            public uint Fg, Bg;
            public bool HasBg, Bold, Italic, Strike, Over;
            public int Under;       // 0 none, 1 single, 2 double, 3 curly, 4 dotted, 5 dashed
        }

        public int Frames { get; private set; }

        /// <summary>Take the engine's state and draw what changed (all of it when full).</summary>
        void Frame(bool full)
        {
            Interlocked.Exchange(ref _frameQueued, 0);
            _lastFrame = DateTime.UtcNow;
            if (_e == null || !IsVisible) { if (_e == null) ClearRows(); return; }
            IntPtr state = _e.State, iter = _e.RowIterator, cells = _e.RowCells;
            int dirty;
            Gt.Rgb dbg, dfg;
            lock (_e.Lock)
            {
                if (_e.Terminal == IntPtr.Zero) return;
                Gt.ghostty_render_state_update(state, _e.Terminal);
                Gt.ghostty_render_state_get(state, Gt.RS_DIRTY, &dirty);
                if (full) dirty = Gt.DIRTY_FULL;
                if (dirty == Gt.DIRTY_FALSE) { DrawCursorLocked(); DrawBar(); return; }
                Gt.ghostty_render_state_get(state, Gt.RS_COLOR_BACKGROUND, &dbg);
                Gt.ghostty_render_state_get(state, Gt.RS_COLOR_FOREGROUND, &dfg);
                _defBg = Color.FromRgb(dbg.R, dbg.G, dbg.B);
                IntPtr it = iter;
                Gt.ghostty_render_state_get(state, Gt.RS_ROW_ITERATOR, &it);
                int y = 0;
                while (Gt.ghostty_render_state_row_iterator_next(iter))
                {
                    bool rowDirty;
                    Gt.ghostty_render_state_row_get(iter, Gt.ROW_DIRTY, &rowDirty);
                    if (rowDirty || dirty == Gt.DIRTY_FULL)
                    {
                        Row(y);
                        ReadRow(y, cells, Pack(dfg), Pack(dbg));
                        Gt.RowSelection sel = new Gt.RowSelection { Size = (nuint)sizeof(Gt.RowSelection) };
                        bool selected = Gt.ghostty_render_state_row_get(iter, Gt.ROW_SELECTION, &sel) == Gt.SUCCESS;
                        DrawRow(y, selected ? sel.StartX : -1, selected ? sel.EndX : -1);
                        bool no = false;
                        Gt.ghostty_render_state_row_set(iter, Gt.ROW_OPT_DIRTY, &no);
                    }
                    y++;
                }
                for (int k = y; k < _rowV.Count; k++) using (_rowV[k].RenderOpen()) { }
                int clean = Gt.DIRTY_FALSE;
                Gt.ghostty_render_state_set(state, Gt.RS_OPT_DIRTY, &clean);
                DrawCursorLocked();
            }
            DrawBar();
            Frames++;
        }

        void ClearRows()
        {
            foreach (DrawingVisual v in _rowV) using (v.RenderOpen()) { }
            using (_cursorV.RenderOpen()) { }
            using (_barV.RenderOpen()) { }
        }

        /// <summary>The row's visual and cell store, made when first needed.</summary>
        void Row(int y)
        {
            while (_rowV.Count <= y)
            {
                var v = new DrawingVisual();
                _rowV.Add(v);
                _kids.Insert(_kids.Count - 2, v);       // under the cursor and the scrollbar
            }
            _rowV[y].Offset = new Vector(PadL, PadT + y * _cellH);
            if (_lines.Length <= y) Array.Resize(ref _lines, y + 1);
        }

        readonly uint[] _cp = new uint[16];
        readonly Dictionary<long, int> _faceOf = new Dictionary<long, int>();

        void ReadRow(int y, IntPtr cells, uint defFg, uint defBg)
        {
            IntPtr c = cells;
            Gt.ghostty_render_state_row_get(_e.RowIterator, Gt.ROW_CELLS, &c);
            int cols = Math.Max(1, _e.Cols);
            if (_lines[y] == null || _lines[y].Length != cols) _lines[y] = new Cell[cols];
            Cell[] line = _lines[y];
            int x = 0;
            while (x < cols && Gt.ghostty_render_state_row_cells_next(cells))
            {
                uint len;
                Gt.ghostty_render_state_row_cells_get(cells, Gt.CELL_GRAPHEMES_LEN, &len);
                uint cp = 0;
                if (len > 0)
                {
                    fixed (uint* p = _cp)
                    {
                        var buf = new Gt.CodepointBuffer { Ptr = p, Cap = (nuint)_cp.Length };
                        Gt.ghostty_render_state_row_cells_get(cells, Gt.CELL_GRAPHEMES_BUF, &buf);
                    }
                    cp = _cp[0];
                }
                Gt.Rgb rgb;
                bool hasFg = Gt.ghostty_render_state_row_cells_get(cells, Gt.CELL_FG, &rgb) == Gt.SUCCESS;
                uint fg = hasFg ? Pack(rgb) : defFg;
                bool hasBg = Gt.ghostty_render_state_row_cells_get(cells, Gt.CELL_BG, &rgb) == Gt.SUCCESS;
                uint bg = hasBg ? Pack(rgb) : defBg;
                var cell = new Cell { Cp = cp, Fg = fg, Bg = bg, HasBg = hasBg };
                bool styled;
                Gt.ghostty_render_state_row_cells_get(cells, Gt.CELL_HAS_STYLING, &styled);
                if (styled)
                {
                    Gt.Style st;
                    Gt.ghostty_render_state_row_cells_get(cells, Gt.CELL_STYLE, &st);
                    ushort f = st.Flags;
                    cell.Bold = (f & Gt.FLAG_BOLD) != 0;
                    cell.Italic = (f & Gt.FLAG_ITALIC) != 0;
                    cell.Strike = (f & Gt.FLAG_STRIKETHROUGH) != 0;
                    cell.Over = (f & Gt.FLAG_OVERLINE) != 0;
                    cell.Under = (f & Gt.UNDERLINE_MASK) >> Gt.UNDERLINE_SHIFT;
                    if ((f & Gt.FLAG_INVERSE) != 0) { uint t = cell.Fg; cell.Fg = cell.Bg; cell.Bg = t; cell.HasBg = true; }
                    if ((f & Gt.FLAG_FAINT) != 0) cell.Fg = (cell.Fg & 0x00FFFFFF) | 0x80000000;
                    if ((f & Gt.FLAG_INVISIBLE) != 0) cell.Cp = 0;
                }
                cell.Face = FaceFor(cell.Cp, cell.Bold, cell.Italic);
                line[x++] = cell;
            }
            for (; x < cols; x++) line[x] = new Cell { Fg = defFg, Bg = defBg };
        }

        GlyphTypeface Typeface(int face, bool bold, bool italic)
        {
            Face f = _faces[Math.Min(face, _faces.Length - 1)];
            return bold ? (italic ? f.BoldItalic : f.Bold) : italic ? f.Italic : f.Normal;
        }

        /// <summary>The first font that has the character (the text's own, then Cascadia Mono, Consolas, symbols, emoji, CJK …).</summary>
        int FaceFor(uint cp, bool bold, bool italic)
        {
            if (cp <= ' ') return 0;
            long key = cp | ((long)(bold ? 1 : 0) << 32) | ((long)(italic ? 1 : 0) << 33);
            int i;
            if (_faceOf.TryGetValue(key, out i)) return i;
            i = 0;
            for (int k = 0; k < _faces.Length; k++)
            {
                ushort g;
                if (Typeface(k, bold, italic).CharacterToGlyphMap.TryGetValue((int)cp, out g) && g != 0) { i = k; break; }
                if (k == _faces.Length - 1) i = -1;
            }
            _faceOf[key] = i;
            return i;
        }

        readonly List<ushort> _glyphs = new List<ushort>(256);
        readonly List<double> _adv = new List<double>(256);

        void DrawRow(int y, int selFrom, int selTo)
        {
            Cell[] line = _lines[y];
            int n = line.Length;
            using (DrawingContext dc = _rowV[y].RenderOpen())
            {
                // backgrounds: one rectangle per run of the same colour; the default background is the window's (or the solid colour)
                int s = 0;
                for (int i = 1; i <= n; i++)
                {
                    if (i < n && line[i].HasBg == line[s].HasBg && line[i].Bg == line[s].Bg) continue;
                    if (line[s].HasBg) dc.DrawRectangle(BrushOf(line[s].Bg), null, new Rect(s * _cellW, 0, (i - s) * _cellW, _cellH));
                    s = i;
                }
                if (selFrom >= 0 && selTo >= selFrom)
                    dc.DrawRectangle(_selBrush, null, new Rect(selFrom * _cellW, 0, (Math.Min(selTo, n - 1) - selFrom + 1) * _cellW, _cellH));
                // text: one GlyphRun per run of the same colour, font and style
                s = 0;
                for (int i = 1; i <= n; i++)
                {
                    if (i < n && line[i].Fg == line[s].Fg && line[i].Face == line[s].Face && line[i].Bold == line[s].Bold && line[i].Italic == line[s].Italic) continue;
                    Run(dc, line, s, i);
                    s = i;
                }
                // lines: underline (single / double / curly … all drawn as one or two), strikethrough, overline
                for (int i = 0; i < n; i++)
                {
                    Cell c = line[i];
                    if (c.Under == 0 && !c.Strike && !c.Over) continue;
                    var pen = new Pen(BrushOf(c.Fg), Math.Max(1 / _dip, Math.Round(_fontSize / 14.0 * _dip) / _dip));
                    double x0 = i * _cellW, x1 = x0 + _cellW;
                    if (c.Under > 0)
                    {
                        double uy = Math.Min(_cellH - pen.Thickness / 2, _baseline + 2 / _dip);
                        dc.DrawLine(pen, new Point(x0, uy), new Point(x1, uy));
                        if (c.Under == 2) dc.DrawLine(pen, new Point(x0, uy - 2 * pen.Thickness), new Point(x1, uy - 2 * pen.Thickness));
                    }
                    if (c.Strike) { double sy = Math.Round(_cellH * 0.55 * _dip) / _dip; dc.DrawLine(pen, new Point(x0, sy), new Point(x1, sy)); }
                    if (c.Over) dc.DrawLine(pen, new Point(x0, pen.Thickness / 2), new Point(x1, pen.Thickness / 2));
                }
            }
        }

        void Run(DrawingContext dc, Cell[] line, int s, int e)
        {
            int face = line[s].Face;
            if (face < 0) face = 0;
            GlyphTypeface tf = Typeface(face, line[s].Bold, line[s].Italic);
            _glyphs.Clear(); _adv.Clear();
            bool any = false;
            ushort space, missing;
            tf.CharacterToGlyphMap.TryGetValue(' ', out space);
            if (!tf.CharacterToGlyphMap.TryGetValue('?', out missing)) missing = space;
            for (int k = s; k < e; k++)
            {
                uint cp = line[k].Cp;
                ushort g = space;
                if (cp > ' ')
                {
                    if (line[k].Face < 0 || !tf.CharacterToGlyphMap.TryGetValue((int)cp, out g)) g = missing;
                    any = true;
                }
                _glyphs.Add(g);
                _adv.Add(_cellW);
            }
            if (!any) return;
            try
            {
                var run = new GlyphRun(tf, 0, false, _fontSize, (float)_dip, _glyphs.ToArray(), new Point(s * _cellW, _baseline), _adv.ToArray(),
                                       null, null, null, null, null, null);
                dc.DrawGlyphRun(BrushOf(line[s].Fg), run);
            }
            catch (Exception ex) { Log.Write("Terminal draw: " + ex.Message); }
        }

        int _curX = -1, _curY = -1, _curStyle = Gt.CURSOR_BLOCK;
        bool _curVisible, _curBlinks;

        void DrawCursor()
        {
            if (_e == null) return;
            lock (_e.Lock) DrawCursorLocked();
        }

        void DrawCursorLocked()
        {
            IntPtr state = _e.State;
            bool has = false, vis = false, blink = false;
            ushort cx = 0, cy = 0;
            int style = Gt.CURSOR_BLOCK;
            Gt.ghostty_render_state_get(state, Gt.RS_CURSOR_VIEWPORT_HAS_VALUE, &has);
            Gt.ghostty_render_state_get(state, Gt.RS_CURSOR_VISIBLE, &vis);
            Gt.ghostty_render_state_get(state, Gt.RS_CURSOR_BLINKING, &blink);
            Gt.ghostty_render_state_get(state, Gt.RS_CURSOR_VISUAL_STYLE, &style);
            if (has) { Gt.ghostty_render_state_get(state, Gt.RS_CURSOR_VIEWPORT_X, &cx); Gt.ghostty_render_state_get(state, Gt.RS_CURSOR_VIEWPORT_Y, &cy); }
            Gt.Rgb cc;
            Color col = Gt.ghostty_render_state_get(state, Gt.RS_COLOR_CURSOR, &cc) == Gt.SUCCESS ? Color.FromRgb(cc.R, cc.G, cc.B) : _cursorColor;
            bool moved = cx != _curX || cy != _curY;
            _curX = has ? cx : -1; _curY = has ? cy : -1; _curStyle = style; _curVisible = has && vis; _curBlinks = blink;
            if (moved) { _blinkOn = true; if (_blink.IsEnabled) { _blink.Stop(); _blink.Start(); } }
            if (_focused && _curBlinks) { if (!_blink.IsEnabled) _blink.Start(); } else { _blink.Stop(); _blinkOn = true; }
            using (DrawingContext dc = _cursorV.RenderOpen())
            {
                if (!_curVisible || (!_blinkOn && _focused)) return;
                double x = PadL + cx * _cellW, y = PadT + cy * _cellH;
                bool wide = cy < _lines.Length && _lines[cy] != null && cx + 1 < _lines[cy].Length && _lines[cy][cx].Cp > 0x1100 && _lines[cy][cx + 1].Cp == 0;
                double w = _cellW * (wide ? 2 : 1);
                Brush b = BrushOf(Pack(col));
                double t = Math.Max(1, Math.Round(_dip)) / _dip;
                if (!_focused || style == Gt.CURSOR_BLOCK_HOLLOW)
                    dc.DrawRectangle(null, new Pen(b, t), new Rect(x + t / 2, y + t / 2, w - t, _cellH - t));
                else if (style == Gt.CURSOR_BAR) dc.DrawRectangle(b, null, new Rect(x, y, 2 * t, _cellH));
                else if (style == Gt.CURSOR_UNDERLINE) dc.DrawRectangle(b, null, new Rect(x, y + _cellH - 2 * t, w, 2 * t));
                else
                {
                    dc.DrawRectangle(b, null, new Rect(x, y, w, _cellH));
                    // the character under the block, in the background colour
                    if (cy < _lines.Length && _lines[cy] != null && cx < _lines[cy].Length && _lines[cy][cx].Cp > ' ')
                    {
                        Cell c = _lines[cy][cx];
                        c.Fg = Pack(_defBg);
                        Cell[] one = { c };
                        dc.PushTransform(new TranslateTransform(x - 0, y));
                        Run(dc, one, 0, 1);
                        dc.Pop();
                    }
                }
            }
            MoveIme();
        }

        // ---- the scrollbar: a thin thumb on the right while there's scrollback (as the other vSuite apps': wider when pointed at)
        Gt.Scrollbar _sb;
        bool _barHot, _barDrag;
        double _barGrab;

        void DrawBar()
        {
            if (_e == null) return;
            _sb = _e.Scrollbar();
            using (DrawingContext dc = _barV.RenderOpen())
            {
                Rect r = ThumbRect();
                if (r.IsEmpty) return;
                dc.DrawRoundedRectangle(_thumb, null, r, r.Width / 2, r.Width / 2);
            }
        }

        Rect ThumbRect()
        {
            if (_sb.Total <= _sb.Len || _sb.Total == 0 || ActualHeight < 20) return Rect.Empty;
            double track = ActualHeight - 4;
            double h = Math.Max(40, track * _sb.Len / _sb.Total);
            double top = 2 + (track - h) * _sb.Offset / Math.Max(1, _sb.Total - _sb.Len);
            double w = _barHot || _barDrag ? 10 : 6;
            return new Rect(ActualWidth - w - 1, top, w, h);
        }

        // =================================================================
        // Keys
        // =================================================================
        void Send(byte[] b)
        {
            if (b == null || b.Length == 0 || Input == null) return;
            Input(b);
        }

        /// <summary>Typed: the view goes back to the bottom, the selection goes.</summary>
        void Typed()
        {
            if (_e == null) return;
            if (_sb.Offset + _sb.Len < _sb.Total) _e.ScrollToBottom();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_e == null || e.Handled) return;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k == Key.ImeProcessed || k == Key.DeadCharProcessed) return;
            if (PressKey(k, Keyboard.Modifiers, e.IsRepeat)) e.Handled = true;
        }

        /// <summary>
        /// A key pressed (the tests' "key" command comes here too): vTerm's own (copy, paste, scrolling) or the program's, encoded the way
        /// it asked (cursor keys, kitty keyboard …). Keys that type text without Ctrl / Alt are left to TextInput. True = handled.
        /// </summary>
        public bool PressKey(Key k, ModifierKeys m, bool repeat)
        {
            if (_e == null) return false;
            bool ctrl = (m & ModifierKeys.Control) != 0, shift = (m & ModifierKeys.Shift) != 0, alt = (m & ModifierKeys.Alt) != 0;

            // vTerm's own: copy / paste, scrolling the scrollback
            if (ctrl && !alt && k == System.Windows.Input.Key.C && (shift || _e.HasSelection)) { Copy(); return true; }
            if (ctrl && !alt && k == System.Windows.Input.Key.V) { Paste(); return true; }
            if (shift && !ctrl && !alt && k == System.Windows.Input.Key.Insert) { Paste(); return true; }
            if (shift && !ctrl && !alt && (k == System.Windows.Input.Key.PageUp || k == System.Windows.Input.Key.PageDown) && !_e.AltScreen)
                { _e.ScrollBy((k == System.Windows.Input.Key.PageUp ? -1 : 1) * Math.Max(1, _e.Rows - 1)); return true; }
            if (ctrl && shift && !alt && (k == System.Windows.Input.Key.Home || k == System.Windows.Input.Key.End))
                { if (k == System.Windows.Input.Key.Home) _e.ScrollToRow(0); else _e.ScrollToBottom(); return true; }
            if (alt && !ctrl && (k == System.Windows.Input.Key.F4 || k == System.Windows.Input.Key.Space)) return false;   // close / the window menu, as anywhere

            int gk = KeyMap.Ghostty(k);
            if (gk == 0) return false;
            string text = KeyMap.Text(k, shift, false), plain = KeyMap.Text(k, false, false);
            bool printable = !string.IsNullOrEmpty(text) && text[0] >= ' ' && text != "\x7f";
            // keys that type text (no Ctrl / Alt, or AltGr's characters) come through TextInput
            if (printable && !ctrl && !alt) return false;
            if (ctrl && alt && !string.IsNullOrEmpty(KeyMap.Text(k, shift, true))) return false;
            uint unshifted = !string.IsNullOrEmpty(plain) && plain[0] >= ' ' ? (uint)char.ConvertToUtf32(plain, 0) : 0;
            byte[] b = _e.EncodeKey(gk, repeat ? Gt.KEY_REPEAT : Gt.KEY_PRESS, Mods(m), (ushort)(printable && shift ? Gt.MODS_SHIFT : 0),
                                    printable ? text : "", unshifted);
            if (b.Length == 0 && !KeyMap.Special(k)) return false;
            Typed();
            Send(b);
            return true;
        }

        /// <summary>Typed text (the tests' "text" command; TextInput does the same).</summary>
        public void TypeText(string text)
        {
            if (_e == null || string.IsNullOrEmpty(text)) return;
            Typed();
            Send(Encoding.UTF8.GetBytes(text));
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (_e == null) return;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            int gk = KeyMap.Ghostty(k);
            if (gk == 0) return;
            // only programs that asked for key releases (the kitty keyboard protocol) get anything
            byte[] b = _e.EncodeKey(gk, Gt.KEY_RELEASE, Mods(Keyboard.Modifiers), 0, "", 0);
            Send(b);
        }

        static ushort Mods(ModifierKeys m)
        {
            ushort r = 0;
            if ((m & ModifierKeys.Shift) != 0) r |= Gt.MODS_SHIFT;
            if ((m & ModifierKeys.Control) != 0) r |= Gt.MODS_CTRL;
            if ((m & ModifierKeys.Alt) != 0) r |= Gt.MODS_ALT;
            if ((m & ModifierKeys.Windows) != 0) r |= Gt.MODS_SUPER;
            if (Keyboard.IsKeyToggled(Key.CapsLock)) r |= Gt.MODS_CAPS;
            if (Keyboard.IsKeyToggled(Key.NumLock)) r |= Gt.MODS_NUM;
            return r;
        }

        protected override void OnTextInput(TextCompositionEventArgs e)
        {
            base.OnTextInput(e);
            if (_e == null || string.IsNullOrEmpty(e.Text)) return;
            var sb = new StringBuilder(e.Text.Length);
            foreach (char c in e.Text) if (c >= ' ' && c != '\x7f') sb.Append(c);
            if (sb.Length == 0) return;
            e.Handled = true;
            Typed();
            Send(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            _focused = true;
            _blinkOn = true;
            if (_e != null) { Send(_e.EncodeFocus(true)); DrawCursor(); }
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            _focused = false;
            if (_e != null) { Send(_e.EncodeFocus(false)); DrawCursor(); }
        }

        // ---- the IME's window at the cursor (Japanese, Chinese, Korean …)
        [StructLayout(LayoutKind.Sequential)] struct COMPOSITIONFORM { public int dwStyle; public POINT ptCurrentPos; public RECT rcArea; }
        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        [DllImport("imm32.dll")] static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref COMPOSITIONFORM lpCompForm);

        void MoveIme()
        {
            if (!_focused || _curX < 0) return;
            try
            {
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src == null) return;
                Point p = TransformToAncestor(src.RootVisual).Transform(new Point(PadL + _curX * _cellW, PadT + _curY * _cellH));
                IntPtr imc = ImmGetContext(src.Handle);
                if (imc == IntPtr.Zero) return;
                var cf = new COMPOSITIONFORM { dwStyle = 0x0002, ptCurrentPos = new POINT { X = (int)Math.Round(p.X * _dip), Y = (int)Math.Round(p.Y * _dip) } };
                ImmSetCompositionWindow(imc, ref cf);
                ImmReleaseContext(src.Handle, imc);
            }
            catch { }
        }

        // =================================================================
        // Clipboard
        // =================================================================
        public void Copy()
        {
            if (_e == null) return;
            string t = _e.SelectedText();
            if (t.Length == 0) return;
            try { Clipboard.SetText(t); } catch (Exception ex) { Log.Write("Terminal copy: " + ex.Message); }
            _e.ClearSelection();
        }

        public void Paste()
        {
            if (_e == null) return;
            string text = "";
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            if (text.Length == 0) return;
            bool safe;
            Typed();
            Send(_e.EncodePaste(text.Replace("\r\n", "\r").Replace("\n", "\r"), out safe));
        }

        // =================================================================
        // Mouse
        // =================================================================
        bool _gesturing, _mouseToApp;
        int _appButton;

        int ColAt(Point p) { return (int)Math.Floor((p.X - PadL) / _cellW); }
        int RowAt(Point p) { return (int)Math.Floor((p.Y - PadT) / _cellH); }

        Gt.GestureGeometry Geometry()
        {
            return new Gt.GestureGeometry
            {
                Columns = (uint)Math.Max(1, _e.Cols), CellWidth = (uint)CellWidthPx, PaddingLeft = (uint)Math.Round(PadL * _dip),
                ScreenHeight = (uint)Math.Round(ActualHeight * _dip)
            };
        }

        static ulong NowNs() { return (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1e9 / System.Diagnostics.Stopwatch.Frequency)); }

        Gt.MouseEncoderSize EncSize()
        {
            return new Gt.MouseEncoderSize
            {
                ScreenWidth = (uint)Math.Round(ActualWidth * _dip), ScreenHeight = (uint)Math.Round(ActualHeight * _dip),
                CellWidth = (uint)CellWidthPx, CellHeight = (uint)CellHeightPx, PaddingTop = (uint)Math.Round(PadT * _dip),
                PaddingBottom = (uint)Math.Round(PadB * _dip), PaddingLeft = (uint)Math.Round(PadL * _dip), PaddingRight = (uint)Math.Round(BarW * _dip)
            };
        }

        /// <summary>The mouse goes to the program (it asked for it), unless Shift is held - then it selects, as in other terminals.</summary>
        bool ToApp() { return _e != null && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && _e.MouseTracking; }

        void AppMouse(int action, int button, Point p)
        {
            Send(_e.EncodeMouse(action, button, Mods(Keyboard.Modifiers), (float)(p.X * _dip), (float)(p.Y * _dip), EncSize(), _appButton != 0));
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (_e == null) return;
            Focus();
            Keyboard.Focus(this);
            Point p = e.GetPosition(this);
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Left && !ThumbRect().IsEmpty && p.X >= ActualWidth - BarW) { BarPress(p); return; }
            if (ToApp())
            {
                int btn = e.ChangedButton == MouseButton.Left ? Gt.MOUSE_LEFT : e.ChangedButton == MouseButton.Right ? Gt.MOUSE_RIGHT : e.ChangedButton == MouseButton.Middle ? Gt.MOUSE_MIDDLE : 0;
                if (btn == 0) return;
                _appButton = btn;
                _mouseToApp = true;
                CaptureMouse();
                AppMouse(Gt.MOUSE_PRESS, btn, p);
                return;
            }
            if (e.ChangedButton == MouseButton.Left)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    string link = LinkAt(p);
                    if (link != null) { if (LinkOpened != null) LinkOpened(link); return; }
                }
                _gesturing = true;
                CaptureMouse();
                _e.Gesture(Gt.GESTURE_PRESS, ColAt(p), RowAt(p), p.X * _dip, p.Y * _dip, Geometry(), NowNs());
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                // as before: right-click copies what's selected, or pastes when nothing is
                if (_e.HasSelection) Copy(); else Paste();
            }
            else if (e.ChangedButton == MouseButton.Middle) Paste();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_e == null) return;
            Point p = e.GetPosition(this);
            if (_barDrag) { BarDrag(p); return; }
            bool hot = !ThumbRect().IsEmpty && p.X >= ActualWidth - BarW;
            if (hot != _barHot) { _barHot = hot; DrawBar(); }
            if (_mouseToApp || (ToApp() && !_gesturing)) { AppMouse(Gt.MOUSE_MOTION, _appButton, p); return; }
            if (_gesturing && e.LeftButton == MouseButtonState.Pressed)
            {
                _e.Gesture(Gt.GESTURE_DRAG, ColAt(p), RowAt(p), p.X * _dip, p.Y * _dip, Geometry(), 0);
                _lastDrag = p;
                if (_e.AutoscrollDirection() != 0) { if (!_autoscroll.IsEnabled) _autoscroll.Start(); } else _autoscroll.Stop();
                return;
            }
            Cursor = hot ? Cursors.Arrow : (Keyboard.Modifiers & ModifierKeys.Control) != 0 && LinkAt(p) != null ? Cursors.Hand : Cursors.IBeam;
        }

        Point _lastDrag;

        void AutoscrollTick()
        {
            if (!_gesturing || _e == null) { _autoscroll.Stop(); return; }
            int dir = _e.AutoscrollDirection();
            if (dir == 0) { _autoscroll.Stop(); return; }
            Point p = _lastDrag;
            _e.Gesture(Gt.GESTURE_AUTOSCROLL_TICK, ColAt(p), Math.Max(0, Math.Min(_e.Rows - 1, RowAt(p))), p.X * _dip, p.Y * _dip, Geometry(), 0);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (_e == null) return;
            Point p = e.GetPosition(this);
            if (_barDrag) { _barDrag = false; ReleaseMouseCapture(); DrawBar(); return; }
            if (_mouseToApp)
            {
                int btn = e.ChangedButton == MouseButton.Left ? Gt.MOUSE_LEFT : e.ChangedButton == MouseButton.Right ? Gt.MOUSE_RIGHT : e.ChangedButton == MouseButton.Middle ? Gt.MOUSE_MIDDLE : 0;
                AppMouse(Gt.MOUSE_RELEASE, btn, p);
                _appButton = 0;
                _mouseToApp = false;
                ReleaseMouseCapture();
                return;
            }
            if (_gesturing && e.ChangedButton == MouseButton.Left)
            {
                _gesturing = false;
                _autoscroll.Stop();
                ReleaseMouseCapture();
                _e.Gesture(Gt.GESTURE_RELEASE, ColAt(p), RowAt(p), p.X * _dip, p.Y * _dip, Geometry(), 0);
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_barHot) { _barHot = false; DrawBar(); }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_e == null) return;
            e.Handled = true;
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);
            Point p = e.GetPosition(this);
            if (ToApp())
            {
                int btn = notches > 0 ? Gt.MOUSE_FOUR : Gt.MOUSE_FIVE;
                for (int i = 0; i < Math.Abs(notches); i++) AppMouse(Gt.MOUSE_PRESS, btn, p);
                return;
            }
            if (_e.AltScreen)
            {
                // a full-screen program (less, vim …) without the mouse: the wheel is arrow keys, as in other terminals
                int key = notches > 0 ? KeyMap.Ghostty(Key.Up) : KeyMap.Ghostty(Key.Down);
                for (int i = 0; i < Math.Abs(notches) * 3; i++) Send(_e.EncodeKey(key, Gt.KEY_PRESS, 0, 0, "", 0));
                return;
            }
            _e.ScrollBy(-notches * 3);
        }

        /// <summary>The tests' "drag" command: press at one cell, drag to another, let go - the same gestures the mouse makes.</summary>
        public void TestDrag(int c1, int r1, int c2, int r2)
        {
            if (_e == null) return;
            Func<int, double> px = c => (PadL + (c + 0.5) * _cellW) * _dip;
            Func<int, double> py = r => (PadT + (r + 0.5) * _cellH) * _dip;
            _e.Gesture(Gt.GESTURE_PRESS, c1, r1, px(c1), py(r1), Geometry(), NowNs());
            _e.Gesture(Gt.GESTURE_DRAG, c2, r2, px(c2), py(r2), Geometry(), 0);
            _e.Gesture(Gt.GESTURE_RELEASE, c2, r2, px(c2), py(r2), Geometry(), 0);
        }

        /// <summary>The tests' "wheel" command: notches up (positive) or down, at the middle of the screen.</summary>
        public void TestWheel(int notches)
        {
            var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, notches * 120) { RoutedEvent = MouseWheelEvent };
            OnMouseWheel(e);
        }

        void BarPress(Point p)
        {
            Rect r = ThumbRect();
            if (r.Contains(new Point(r.X, p.Y))) { _barDrag = true; _barGrab = p.Y - r.Y; CaptureMouse(); DrawBar(); return; }
            _e.ScrollBy((p.Y < r.Y ? -1 : 1) * Math.Max(1, _e.Rows - 1));
        }

        void BarDrag(Point p)
        {
            Rect r = ThumbRect();
            if (r.IsEmpty) return;
            double track = ActualHeight - 4 - r.Height;
            double f = Math.Max(0, Math.Min(1, (p.Y - _barGrab - 2) / Math.Max(1, track)));
            _e.ScrollToRow((long)Math.Round(f * (_sb.Total - _sb.Len)));
        }

        // ---- links: Ctrl+click an address shown on the screen
        static readonly Regex Url = new Regex(@"(https?|ftp|file)://[^\s<>""'`{}|\\^]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        string LinkAt(Point p)
        {
            int y = RowAt(p), x = ColAt(p);
            if (y < 0 || y >= _lines.Length || _lines[y] == null || x < 0 || x >= _lines[y].Length) return null;
            var sb = new StringBuilder();
            var colOf = new List<int>();
            for (int i = 0; i < _lines[y].Length; i++)
            {
                uint cp = _lines[y][i].Cp;
                if (cp == 0 && i > 0 && _lines[y][i - 1].Cp > 0x1100) continue;     // a wide character's second half
                string s = cp == 0 ? " " : char.ConvertFromUtf32((int)cp);
                foreach (char c in s) { sb.Append(c); colOf.Add(i); }
            }
            foreach (Match m in Url.Matches(sb.ToString()))
            {
                string u = m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '!', '?');
                int from = colOf[m.Index], to = colOf[Math.Min(colOf.Count - 1, m.Index + u.Length - 1)];
                if (x >= from && x <= to) return u;
            }
            return null;
        }

        static uint Pack(Gt.Rgb c) { return 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B; }
        static uint Pack(Color c) { return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B; }
    }
}
