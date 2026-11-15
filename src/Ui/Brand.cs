using System.Windows;
using System.Windows.Media;

namespace vTerm.Ui
{
    /// <summary>
    /// vTerm's icon drawing (AppIcon.Drawer, set in Program), on a 64 x 64 grid like vXplorer's folder and vView's play shape: a terminal
    /// window outline in the Main Accent, filled with the theme's background, holding a prompt chevron and vShell's three-dot mark as the
    /// cursor (Main Accent, Secondary Accent, text). small: heavier lines and bigger dots for 16-24 px.
    /// `vTerm.exe --make-icon vterm.ico` writes it in vShell's default colours for the exe's own icon.
    /// </summary>
    public static class Brand
    {
        static readonly Geometry Frame = Geometry.Parse("M8,17 A6,6 0 0 1 14,11 H50 A6,6 0 0 1 56,17 V47 A6,6 0 0 1 50,53 H14 A6,6 0 0 1 8,47 Z");

        public static Drawing Draw(Color main, Color secondary, Color text, Color background, bool small)
        {
            // small: the dots a little bigger but closer, so the last one stays clear of the window's edge
            double stroke = small ? 5.5 : 4, r = small ? 4 : 3.6, x0 = small ? 31 : 31.5, step = small ? 2.4 : 2.5;
            var g = new DrawingGroup();
            var pen = new Pen(AppIcon.Brush(main), stroke) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            g.Children.Add(new GeometryDrawing(AppIcon.Brush(background), pen, Frame));
            // the prompt: >
            var chev = new Pen(AppIcon.Brush(text), small ? 5.5 : 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            chev.Freeze();
            g.Children.Add(new GeometryDrawing(null, chev, Geometry.Parse(small ? "M16,23 L25,31 L16,39" : "M18,24 L26,31 L18,38")));
            // the cursor: vShell's three dots
            const double y = 39;
            g.Children.Add(new GeometryDrawing(AppIcon.Brush(main), null, new EllipseGeometry(new Point(x0, y), r, r)));
            g.Children.Add(new GeometryDrawing(AppIcon.Brush(secondary), null, new EllipseGeometry(new Point(x0 + r * step, y), r, r)));
            g.Children.Add(new GeometryDrawing(AppIcon.Brush(text), null, new EllipseGeometry(new Point(x0 + r * step * 2, y), r, r)));
            g.Freeze();
            return g;
        }
    }
}
