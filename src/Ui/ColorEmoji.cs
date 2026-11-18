using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// Emoji in colour: WPF can't draw colour fonts, so an emoji cell's whole cluster (skin tones, joined emoji …) is drawn by Windows'
    /// own text engine - DirectWrite through Direct2D, onto a WIC bitmap, with colour fonts on - the way Windows Terminal and Edge draw
    /// them. Each one is drawn once per size and kept. Plain COM through function pointers (no package). If any of it fails, Available
    /// goes false and the terminal draws emoji in one colour as before.
    /// </summary>
    static unsafe class ColorEmoji
    {
        static IntPtr _d2d, _dw, _wic;
        static bool _tried, _failed;
        static readonly Dictionary<string, BitmapSource> Cache = new Dictionary<string, BitmapSource>();
        static readonly Dictionary<int, IntPtr> Formats = new Dictionary<int, IntPtr>();

        static Guid IID_ID2D1Factory = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
        static Guid IID_IDWriteFactory = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        static Guid CLSID_WICImagingFactory = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
        static Guid IID_IWICImagingFactory = new Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
        static Guid GUID_WICPixelFormat32bppPBGRA = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc910");

        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr options, out IntPtr factory);
        [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, ref Guid riid, out IntPtr factory);
        [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int ctx, ref Guid riid, out IntPtr obj);

        [StructLayout(LayoutKind.Sequential)] struct ColorF { public float R, G, B, A; }
        [StructLayout(LayoutKind.Sequential)] struct RectF { public float L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)]
        struct RenderTargetProperties { public int Type, DxgiFormat, AlphaMode; public float DpiX, DpiY; public int Usage, MinLevel; }

        static IntPtr Slot(IntPtr obj, int i) { return (*(IntPtr**)obj)[i]; }
        static void Release(IntPtr obj) { if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj); }

        public static bool Available { get { return Start(); } }

        static bool Start()
        {
            if (_tried) return !_failed;
            _tried = true;
            try
            {
                int hr = D2D1CreateFactory(0, ref IID_ID2D1Factory, IntPtr.Zero, out _d2d);           // single-threaded: the UI thread only
                if (hr >= 0) hr = DWriteCreateFactory(0, ref IID_IDWriteFactory, out _dw);              // shared
                if (hr >= 0) hr = CoCreateInstance(ref CLSID_WICImagingFactory, IntPtr.Zero, 1, ref IID_IWICImagingFactory, out _wic);
                if (hr < 0) { _failed = true; Log.Write("Colour emoji: couldn't start Direct2D / DirectWrite (0x" + hr.ToString("X8") + ") - emoji in one colour"); }
            }
            catch (Exception ex) { _failed = true; Log.Write("Colour emoji: " + ex.Message + " - emoji in one colour"); }
            return !_failed;
        }

        /// <summary>A text format for Segoe UI Emoji at this size (in pixels), centred both ways, never wrapped.</summary>
        static IntPtr Format(float size)
        {
            int key = (int)Math.Round(size * 4);
            IntPtr f;
            if (Formats.TryGetValue(key, out f)) return f;
            fixed (char* family = "Segoe UI Emoji")
            fixed (char* locale = "en-us")
            {
                // IDWriteFactory::CreateTextFormat (15): family, collection, weight, style, stretch, size, locale, out format
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int, int, int, float, char*, IntPtr*, int>)Slot(_dw, 15))
                         (_dw, family, IntPtr.Zero, 400, 0, 5, size, locale, &f);
                if (hr < 0) return IntPtr.Zero;
            }
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(f, 3))(f, 2);     // SetTextAlignment: centre
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(f, 4))(f, 2);     // SetParagraphAlignment: centre
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(f, 5))(f, 1);     // SetWordWrapping: none
            Formats[key] = f;
            return f;
        }

        /// <summary>The cluster drawn in colour into a w x h pixel box (fg = the colour for any glyph the font has no colour for); null if
        /// it can't be.</summary>
        public static BitmapSource Get(string cluster, int w, int h, uint fg)
        {
            if (string.IsNullOrEmpty(cluster) || w < 2 || h < 2 || !Start()) return null;
            string key = cluster + "|" + w + "x" + h + "|" + fg.ToString("X8");
            BitmapSource b;
            if (Cache.TryGetValue(key, out b)) return b;
            if (Cache.Count > 3000) Cache.Clear();
            try { b = Draw(cluster, w, h, fg); }
            catch (Exception ex) { Log.Write("Colour emoji: " + ex.Message); b = null; }
            Cache[key] = b;
            return b;
        }

        static BitmapSource Draw(string cluster, int w, int h, uint fg)
        {
            IntPtr bmp = IntPtr.Zero, rt = IntPtr.Zero, brush = IntPtr.Zero;
            try
            {
                // the emoji as big as the box allows, a little room around it
                IntPtr fmt = Format(Math.Max(4f, Math.Min(h, w) * 0.86f));
                if (fmt == IntPtr.Zero) return null;
                Guid pbgra = GUID_WICPixelFormat32bppPBGRA;
                // IWICImagingFactory::CreateBitmap (17): w, h, format, cache on load, out bitmap
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, Guid*, int, IntPtr*, int>)Slot(_wic, 17))(_wic, (uint)w, (uint)h, &pbgra, 2, &bmp);
                if (hr < 0) return null;
                var props = new RenderTargetProperties { Type = 0, DxgiFormat = 87, AlphaMode = 1, DpiX = 96, DpiY = 96 };   // B8G8R8A8, premultiplied
                // ID2D1Factory::CreateWicBitmapRenderTarget (13)
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, RenderTargetProperties*, IntPtr*, int>)Slot(_d2d, 13))(_d2d, bmp, &props, &rt);
                if (hr < 0) return null;
                ((delegate* unmanaged[Stdcall]<IntPtr, int, void>)Slot(rt, 34))(rt, 2);         // SetTextAntialiasMode: grayscale (no ClearType on a see-through bitmap)
                var colour = new ColorF { R = ((fg >> 16) & 0xFF) / 255f, G = ((fg >> 8) & 0xFF) / 255f, B = (fg & 0xFF) / 255f, A = 1 };
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, ColorF*, IntPtr, IntPtr*, int>)Slot(rt, 8))(rt, &colour, IntPtr.Zero, &brush);   // CreateSolidColorBrush
                if (hr < 0) return null;
                ((delegate* unmanaged[Stdcall]<IntPtr, void>)Slot(rt, 48))(rt);                 // BeginDraw
                var clear = new ColorF();
                ((delegate* unmanaged[Stdcall]<IntPtr, ColorF*, void>)Slot(rt, 47))(rt, &clear); // Clear: see-through
                var box = new RectF { L = 0, T = 0, R = w, B = h };
                fixed (char* s = cluster)
                    // DrawText (27): text, length, format, rect, brush, options (4 = colour fonts), measuring (natural)
                    ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, RectF*, IntPtr, int, int, void>)Slot(rt, 27))(rt, s, (uint)cluster.Length, fmt, &box, brush, 4, 0);
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Slot(rt, 49))(rt, IntPtr.Zero, IntPtr.Zero);   // EndDraw
                if (hr < 0) return null;
                int stride = w * 4;
                var px = new byte[stride * h];
                fixed (byte* p = px)
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, byte*, int>)Slot(bmp, 7))(bmp, IntPtr.Zero, (uint)stride, (uint)px.Length, p);   // CopyPixels
                if (hr < 0) return null;
                var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
                src.Freeze();
                return src;
            }
            finally { Release(brush); Release(rt); Release(bmp); }
        }

        /// <summary>
        /// Does this cluster show as an emoji (a colour picture)? Its first character is one that's an emoji by default, or it asks for the
        /// emoji look (U+FE0F), or it's built of emoji parts (a skin tone, a joiner, flag letters, a keycap).
        /// </summary>
        public static bool IsEmoji(uint* cp, int n)
        {
            if (n <= 0) return false;
            for (int i = 1; i < n; i++)
            {
                uint c = cp[i];
                if (c == 0xFE0F || c == 0x200D || c == 0x20E3 || (c >= 0x1F3FB && c <= 0x1F3FF)) return true;
            }
            uint b = cp[0];
            if (b >= 0x1F1E6 && b <= 0x1F1FF) return true;                         // flag letters
            if (b >= 0x1F300 && b <= 0x1F64F) return true;                         // symbols and faces
            if (b >= 0x1F680 && b <= 0x1F6FF) return true;                         // transport and map
            if (b >= 0x1F7E0 && b <= 0x1F7EB) return true;                         // coloured circles / squares
            if (b >= 0x1F900 && b <= 0x1F9FF) return true;                         // more faces, people, food
            if (b >= 0x1FA70 && b <= 0x1FAFF) return true;                         // newer emoji
            if (b == 0x1F004 || b == 0x1F0CF || b == 0x1F18E || (b >= 0x1F191 && b <= 0x1F19A) || b == 0x1F201 || b == 0x1F21A || b == 0x1F22F ||
                (b >= 0x1F232 && b <= 0x1F236) || (b >= 0x1F238 && b <= 0x1F23A) || b == 0x1F250 || b == 0x1F251) return true;
            switch (b)
            {
                // the emoji in the older blocks that are pictures by default (Emoji_Presentation)
                case 0x231A: case 0x231B: case 0x23E9: case 0x23EA: case 0x23EB: case 0x23EC: case 0x23F0: case 0x23F3: case 0x25FD: case 0x25FE:
                case 0x2614: case 0x2615: case 0x267F: case 0x2693: case 0x26A1: case 0x26AA: case 0x26AB: case 0x26BD: case 0x26BE: case 0x26C4:
                case 0x26C5: case 0x26CE: case 0x26D4: case 0x26EA: case 0x26F2: case 0x26F3: case 0x26F5: case 0x26FA: case 0x26FD: case 0x2705:
                case 0x270A: case 0x270B: case 0x2728: case 0x274C: case 0x274E: case 0x2753: case 0x2754: case 0x2755: case 0x2757: case 0x2795:
                case 0x2796: case 0x2797: case 0x27B0: case 0x27BF: case 0x2B1B: case 0x2B1C: case 0x2B50: case 0x2B55:
                    return true;
            }
            return b >= 0x2648 && b <= 0x2653;                                      // the zodiac
        }
    }
}
