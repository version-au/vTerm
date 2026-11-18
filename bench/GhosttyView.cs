using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace GhosttyBench
{
    /// <summary>
    /// A small WPF renderer on libghostty-vt's render state: one DrawingVisual per row, only dirty rows rebuilt, background runs as
    /// rectangles, text as GlyphRuns (Cascadia Mono, falling back per character to other fonts), a block cursor. No selection, links,
    /// IME or ligatures - enough to measure what the drawing costs.
    /// </summary>
    sealed unsafe class GhosttyView : FrameworkElement
    {
        readonly VisualCollection _kids;
        readonly List<DrawingVisual> _rows = new List<DrawingVisual>();
        readonly DrawingVisual _cursor = new DrawingVisual();
        readonly GlyphTypeface[] _faces, _bold;
        readonly double _fontSize;
        readonly Dictionary<uint, Brush> _brushes = new Dictionary<uint, Brush>();
        readonly uint[] _cp = new uint[16];
        IntPtr _state, _iter, _cells;
        double _dip = 1;

        public readonly object TermLock;
        public IntPtr Term { get; private set; }
        public double CellW { get; private set; }
        public double CellH { get; private set; }
        public int Cols { get; private set; }
        public int RowsCount { get; private set; }

        /// <summary>Frames drawn, rows rebuilt, and the time spent (update + rebuilding) on the UI thread.</summary>
        public int Frames, RowsBuilt;
        public double FrameMs;

        public GhosttyView(object termLock, string font, double size)
        {
            TermLock = termLock;
            _fontSize = size;
            _kids = new VisualCollection(this);
            string[] names = { font, "Consolas", "MS Gothic", "Malgun Gothic", "Microsoft YaHei", "Segoe UI Emoji", "Segoe UI Symbol" };
            var faces = new List<GlyphTypeface>(); var bold = new List<GlyphTypeface>();
            foreach (string n in names)
            {
                GlyphTypeface g, b;
                if (!new Typeface(new FontFamily(n), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out g)) continue;
                if (!new Typeface(new FontFamily(n), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal).TryGetGlyphTypeface(out b)) b = g;
                faces.Add(g); bold.Add(b);
            }
            _faces = faces.ToArray(); _bold = bold.ToArray();
            GlyphTypeface f = _faces[0];
            CellW = f.AdvanceWidths[f.CharacterToGlyphMap['0']] * size;
            CellH = Math.Ceiling(f.Height * size);
            _kids.Add(_cursor);
            Gt.ghostty_render_state_new(IntPtr.Zero, out _state);
            Gt.ghostty_render_state_row_iterator_new(IntPtr.Zero, out _iter);
            Gt.ghostty_render_state_row_cells_new(IntPtr.Zero, out _cells);
        }

        public void Attach(IntPtr term, int cols, int rows)
        {
            Term = term; Cols = cols; RowsCount = rows;
            Width = cols * CellW; Height = rows * CellH;
            while (_rows.Count < rows) { var v = new DrawingVisual(); v.Offset = new Vector(0, _rows.Count * CellH); _rows.Add(v); _kids.Insert(_kids.Count - 1, v); }
            ForceFull();
        }

        public void ForceFull()
        {
            if (Term == IntPtr.Zero) return;
            lock (TermLock) Gt.ghostty_render_state_update(_state, Term);
            int full = Gt.DIRTY_FULL;
            Gt.ghostty_render_state_set(_state, Gt.RS_OPT_DIRTY, &full);
        }

        protected override int VisualChildrenCount { get { return _kids.Count; } }
        protected override Visual GetVisualChild(int index) { return _kids[index]; }
        protected override void OnRender(DrawingContext dc) { dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); }

        /// <summary>One frame: take the terminal's state (under the lock), then rebuild the rows that changed. False = nothing changed.</summary>
        public bool Frame(bool forceFull = false)
        {
            if (Term == IntPtr.Zero) return false;
            long t0 = Stopwatch.GetTimestamp();
            _dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            lock (TermLock) Gt.ghostty_render_state_update(_state, Term);
            int dirty;
            Gt.ghostty_render_state_get(_state, Gt.RS_DIRTY, &dirty);
            if (forceFull) dirty = Gt.DIRTY_FULL;
            if (dirty == Gt.DIRTY_FALSE) return false;

            Gt.Rgb dbg, dfg;
            Gt.ghostty_render_state_get(_state, Gt.RS_COLOR_BACKGROUND, &dbg);
            Gt.ghostty_render_state_get(_state, Gt.RS_COLOR_FOREGROUND, &dfg);
            IntPtr iter = _iter;
            Gt.ghostty_render_state_get(_state, Gt.RS_ROW_ITERATOR, &iter);
            int y = 0;
            while (Gt.ghostty_render_state_row_iterator_next(_iter) && y < _rows.Count)
            {
                bool rowDirty;
                Gt.ghostty_render_state_row_get(_iter, Gt.ROW_DIRTY, &rowDirty);
                if (rowDirty || dirty == Gt.DIRTY_FULL)
                {
                    BuildRow(y, dbg, dfg);
                    bool no = false;
                    Gt.ghostty_render_state_row_set(_iter, Gt.ROW_OPT_DIRTY, &no);
                    RowsBuilt++;
                }
                y++;
            }
            int clean = Gt.DIRTY_FALSE;
            Gt.ghostty_render_state_set(_state, Gt.RS_OPT_DIRTY, &clean);
            DrawCursor(dfg);
            Frames++;
            FrameMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            return true;
        }

        // one run of cells drawn alike: same colours, weight and font
        struct Cell { public int Glyph; public int Face; public uint Fg, Bg; public bool Bold; }
        Cell[] _line = new Cell[0];

        void BuildRow(int y, Gt.Rgb dbg, Gt.Rgb dfg)
        {
            IntPtr cells = _cells;
            Gt.ghostty_render_state_row_get(_iter, Gt.ROW_CELLS, &cells);
            if (_line.Length < Cols) _line = new Cell[Cols];
            uint defBg = Pack(dbg), defFg = Pack(dfg);
            int x = 0;
            while (x < Cols && Gt.ghostty_render_state_row_cells_next(_cells))
            {
                uint len;
                Gt.ghostty_render_state_row_cells_get(_cells, Gt.CELL_GRAPHEMES_LEN, &len);
                uint cp = ' ';
                if (len > 0)
                {
                    fixed (uint* p = _cp)
                    {
                        var buf = new Gt.CodepointBuffer { Ptr = p, Cap = (nuint)_cp.Length };
                        Gt.ghostty_render_state_row_cells_get(_cells, Gt.CELL_GRAPHEMES_BUF, &buf);
                    }
                    cp = _cp[0];
                }
                Gt.Rgb c;
                uint fg = Gt.ghostty_render_state_row_cells_get(_cells, Gt.CELL_FG, &c) == Gt.SUCCESS ? Pack(c) : defFg;
                uint bg = Gt.ghostty_render_state_row_cells_get(_cells, Gt.CELL_BG, &c) == Gt.SUCCESS ? Pack(c) : defBg;
                bool styled, bold = false;
                Gt.ghostty_render_state_row_cells_get(_cells, Gt.CELL_HAS_STYLING, &styled);
                if (styled)
                {
                    Gt.Style st;
                    Gt.ghostty_render_state_row_cells_get(_cells, Gt.CELL_STYLE, &st);
                    bold = (st.Flags & Gt.FLAG_BOLD) != 0;
                    if ((st.Flags & Gt.FLAG_INVERSE) != 0) { uint t = fg; fg = bg; bg = t; }
                    if ((st.Flags & Gt.FLAG_INVISIBLE) != 0) cp = ' ';
                }
                int face = 0, glyph = 0;
                ushort gi;
                GlyphTypeface[] set = bold ? _bold : _faces;
                for (face = 0; face < set.Length; face++)
                    if (set[face].CharacterToGlyphMap.TryGetValue((int)cp, out gi)) { glyph = gi; break; }
                if (face == set.Length) { face = 0; set[0].CharacterToGlyphMap.TryGetValue('?', out gi); glyph = gi; }
                _line[x] = new Cell { Glyph = glyph, Face = face, Fg = fg, Bg = bg, Bold = bold };
                x++;
            }

            using (DrawingContext dc = _rows[y].RenderOpen())
            {
                // backgrounds: one rectangle per run of the same colour (the default background is the window's)
                int s = 0;
                for (int i = 1; i <= x; i++)
                {
                    if (i < x && _line[i].Bg == _line[s].Bg) continue;
                    if (_line[s].Bg != defBg) dc.DrawRectangle(BrushOf(_line[s].Bg), null, new Rect(s * CellW, 0, (i - s) * CellW, CellH));
                    s = i;
                }
                // text: one GlyphRun per run of the same colour, weight and font, spaces left out of the run's ends
                s = 0;
                var glyphs = new List<ushort>(Cols); var adv = new List<double>(Cols);
                for (int i = 1; i <= x; i++)
                {
                    if (i < x && _line[i].Fg == _line[s].Fg && _line[i].Face == _line[s].Face && _line[i].Bold == _line[s].Bold) continue;
                    glyphs.Clear(); adv.Clear();
                    for (int k = s; k < i; k++) { glyphs.Add((ushort)_line[k].Glyph); adv.Add(CellW); }
                    GlyphTypeface face = (_line[s].Bold ? _bold : _faces)[_line[s].Face];
                    if (!AllBlank(s, i))
                    {
                        var run = new GlyphRun(face, 0, false, _fontSize, (float)_dip, glyphs.ToArray(), new Point(s * CellW, Math.Round(_faces[0].Baseline * _fontSize)),
                                               adv.ToArray(), null, null, null, null, null, null);
                        dc.DrawGlyphRun(BrushOf(_line[s].Fg), run);
                    }
                    s = i;
                }
            }
        }

        bool AllBlank(int s, int e)
        {
            int space;
            ushort g;
            space = _faces[0].CharacterToGlyphMap.TryGetValue(' ', out g) ? g : -1;
            for (int k = s; k < e; k++) if (_line[k].Glyph != space || _line[k].Face != 0) return false;
            return true;
        }

        void DrawCursor(Gt.Rgb fg)
        {
            bool has, vis;
            Gt.ghostty_render_state_get(_state, Gt.RS_CURSOR_VIEWPORT_HAS_VALUE, &has);
            Gt.ghostty_render_state_get(_state, Gt.RS_CURSOR_VISIBLE, &vis);
            using (DrawingContext dc = _cursor.RenderOpen())
            {
                if (!has || !vis) return;
                ushort cx, cy;
                Gt.ghostty_render_state_get(_state, Gt.RS_CURSOR_VIEWPORT_X, &cx);
                Gt.ghostty_render_state_get(_state, Gt.RS_CURSOR_VIEWPORT_Y, &cy);
                dc.DrawRectangle(BrushOf(Pack(fg) & 0x00FFFFFF | 0x99000000), null, new Rect(cx * CellW, cy * CellH, CellW, CellH));
            }
        }

        static uint Pack(Gt.Rgb c) { return 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B; }

        Brush BrushOf(uint argb)
        {
            Brush b;
            if (_brushes.TryGetValue(argb, out b)) return b;
            b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            _brushes[argb] = b;
            return b;
        }

        public void Free()
        {
            Gt.ghostty_render_state_row_cells_free(_cells);
            Gt.ghostty_render_state_row_iterator_free(_iter);
            Gt.ghostty_render_state_free(_state);
        }
    }
}
