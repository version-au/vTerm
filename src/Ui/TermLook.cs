using System;
using System.Windows.Media;

namespace vTerm.Ui
{
    /// <summary>
    /// The terminal's brushes, worked out from the suite's Theme the way vShell's own theme does it (dim text = 0x99, hover = 0x22 …) -
    /// the terminal code came from vShell and draws with these. The status colours are vShell's.
    /// </summary>
    public sealed class TermLook
    {
        public SolidColorBrush Fg, FgDim, Hover, MainAccent, SecondaryAccent, Track;
        public Color BgColor;
        public FontFamily Font = new FontFamily("Segoe UI");
        public double FontSize = 12.5;

        public static readonly FontFamily Glyphs = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly SolidColorBrush Good = B(Color.FromRgb(0x6C, 0xCB, 0x7E)), Warn = B(Color.FromRgb(0xF5, 0xC5, 0x42)), Bad = B(Color.FromRgb(0xEF, 0x4D, 0x4D));

        public static TermLook From(Theme t)
        {
            return new TermLook
            {
                Fg = B(t.Fg),
                FgDim = B(Color.FromArgb(0x99, t.Fg.R, t.Fg.G, t.Fg.B)),
                Hover = B(Color.FromArgb(0x22, t.Fg.R, t.Fg.G, t.Fg.B)),
                Track = B(Color.FromArgb(0x30, t.Fg.R, t.Fg.G, t.Fg.B)),
                MainAccent = B(t.Accent),
                SecondaryAccent = B(t.Secondary),
                BgColor = t.Bg
            };
        }

        /// <summary>The brush at percent of its opacity.</summary>
        public static SolidColorBrush Faded(SolidColorBrush b, int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            Color c = b.Color;
            return B(Color.FromArgb((byte)Math.Round(c.A * percent / 100.0), c.R, c.G, c.B));
        }

        /// <summary>A colour setting: empty = fallback, "@main" / "@secondary" / "@text" = that colour, otherwise a colour.</summary>
        public SolidColorBrush Resolve(string spec, SolidColorBrush fallback)
        {
            if (string.IsNullOrWhiteSpace(spec)) return fallback;
            string k = spec.Trim().ToLowerInvariant();
            if (k == "@main" || k == "@accent") return MainAccent;
            if (k == "@secondary" || k == "@spare") return SecondaryAccent;
            if (k == "@text") return Fg;
            try
            {
                object o = ColorConverter.ConvertFromString(spec.Trim());
                if (o is Color) { Color c = (Color)o; return B(Color.FromRgb(c.R, c.G, c.B)); }
            }
            catch { }
            return fallback;
        }

        static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
