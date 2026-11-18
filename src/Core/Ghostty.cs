using System;
using System.Runtime.InteropServices;

namespace vTerm.Core
{
    /// <summary>
    /// libghostty-vt 1.0 (Ghostty's terminal engine as a C library, MIT - lib\libghostty-vt): the parts vTerm uses. Names and numbers
    /// are the C headers' (ghostty/vt/*.h); the C ABI is stable across 1.x.
    /// </summary>
    public static unsafe class Gt
    {
        const string Dll = "ghostty-vt.dll";

        public const int SUCCESS = 0, OUT_OF_SPACE = -3, NO_VALUE = -4;

        // GhosttyTerminalOption
        public const int OPT_USERDATA = 1, OPT_WRITE_PTY = 2, OPT_BELL = 3, OPT_TITLE_CHANGED = 6, OPT_COLOR_FOREGROUND = 12,
                         OPT_COLOR_BACKGROUND = 13, OPT_COLOR_CURSOR = 14, OPT_COLOR_PALETTE = 15, OPT_SELECTION = 22,
                         OPT_SCROLLBACK_MAX_LINES = 29;
        // GhosttyTerminalData
        public const int DATA_ACTIVE_SCREEN = 6, DATA_SCROLLBAR = 9, DATA_MOUSE_TRACKING = 11, DATA_TITLE = 12, DATA_SELECTION = 31,
                         DATA_VIEWPORT_ACTIVE = 32, DATA_MODE = 37, DATA_MEMORY_USAGE = 42;
        // GhosttyTerminalScrollViewportTag
        public const int SCROLL_TOP = 0, SCROLL_BOTTOM = 1, SCROLL_DELTA = 2, SCROLL_ROW = 3;
        // GhosttyRenderStateData
        public const int RS_DIRTY = 3, RS_ROW_ITERATOR = 4, RS_COLOR_BACKGROUND = 5, RS_COLOR_FOREGROUND = 6, RS_COLOR_CURSOR = 7,
                         RS_CURSOR_VISUAL_STYLE = 10, RS_CURSOR_VISIBLE = 11, RS_CURSOR_BLINKING = 12, RS_CURSOR_VIEWPORT_HAS_VALUE = 14,
                         RS_CURSOR_VIEWPORT_X = 15, RS_CURSOR_VIEWPORT_Y = 16;
        public const int RS_OPT_DIRTY = 1;
        public const int DIRTY_FALSE = 0, DIRTY_PARTIAL = 1, DIRTY_FULL = 2;
        // rows / cells of the render state
        public const int ROW_DIRTY = 1, ROW_CELLS = 3, ROW_SELECTION = 4, ROW_OPT_DIRTY = 1;
        public const int CELL_STYLE = 2, CELL_GRAPHEMES_LEN = 3, CELL_GRAPHEMES_BUF = 4, CELL_BG = 5, CELL_FG = 6, CELL_HAS_STYLING = 8;
        // GhosttyCursorStyle
        public const int CURSOR_BAR = 0, CURSOR_BLOCK = 1, CURSOR_UNDERLINE = 2, CURSOR_BLOCK_HOLLOW = 3;
        // GhosttyStyleFlags
        public const ushort FLAG_BOLD = 1, FLAG_ITALIC = 2, FLAG_FAINT = 4, FLAG_BLINK = 8, FLAG_INVERSE = 16, FLAG_INVISIBLE = 32,
                            FLAG_STRIKETHROUGH = 64, FLAG_OVERLINE = 128;
        public const int UNDERLINE_SHIFT = 8, UNDERLINE_MASK = 0x7 << 8;
        // GhosttyPointTag
        public const int POINT_ACTIVE = 0, POINT_VIEWPORT = 1, POINT_SCREEN = 2;
        // key events
        public const int KEY_RELEASE = 0, KEY_PRESS = 1, KEY_REPEAT = 2;
        public const ushort MODS_SHIFT = 1, MODS_CTRL = 2, MODS_ALT = 4, MODS_SUPER = 8, MODS_CAPS = 16, MODS_NUM = 32;
        // mouse events
        public const int MOUSE_PRESS = 0, MOUSE_RELEASE = 1, MOUSE_MOTION = 2;
        public const int MOUSE_LEFT = 1, MOUSE_RIGHT = 2, MOUSE_MIDDLE = 3, MOUSE_FOUR = 4, MOUSE_FIVE = 5;
        public const int MOUSE_ENC_OPT_SIZE = 3, MOUSE_ENC_OPT_ANY_BUTTON_PRESSED = 4, MOUSE_ENC_OPT_TRACK_LAST_CELL = 5;
        // selection gestures
        public const int GESTURE_PRESS = 0, GESTURE_RELEASE = 1, GESTURE_DRAG = 2, GESTURE_AUTOSCROLL_TICK = 3;
        public const int GEV_REF = 1, GEV_POSITION = 2, GEV_TIME_NS = 4, GEV_GEOMETRY = 9, GEV_VIEWPORT = 10;
        public const int GESTURE_DATA_AUTOSCROLL = 3;
        // formatter
        public const int FMT_OPT_EMIT = 1, FMT_OPT_UNWRAP = 2, FMT_OPT_TRIM = 3, FMT_OPT_SELECTION = 4;
        public const int FMT_PLAIN = 0;
        // DEC private modes (GhosttyMode: the number, bit 15 = an ANSI mode)
        public const ushort MODE_FOCUS_EVENT = 1004, MODE_ALT_SCROLL = 1007, MODE_BRACKETED_PASTE = 2004;
        // focus
        public const int FOCUS_GAINED = 0, FOCUS_LOST = 1;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Rgb { public byte R, G, B; }

        /// <summary>GhosttyStyle: three 4-byte colours, then the flags (16 bytes).</summary>
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct Style { [FieldOffset(12)] public ushort Flags; }

        [StructLayout(LayoutKind.Sequential)] public struct GString { public byte* Ptr; public nuint Len; }
        [StructLayout(LayoutKind.Sequential)] public struct CodepointBuffer { public uint* Ptr; public nuint Cap; public nuint Len; }
        [StructLayout(LayoutKind.Sequential)] public struct Scrollbar { public ulong Total, Offset, Len; }
        [StructLayout(LayoutKind.Sequential)] public struct ScrollViewport { public int Tag; public long Value; public long Pad; }
        [StructLayout(LayoutKind.Sequential)] public struct RowSelection { public nuint Size; public ushort StartX, EndX; }
        [StructLayout(LayoutKind.Sequential)] public struct ModeConfig { public ushort Mode; [MarshalAs(UnmanagedType.U1)] public bool Value; }

        [StructLayout(LayoutKind.Sequential)] public struct PointCoordinate { public ushort X; public uint Y; }
        /// <summary>GhosttyPoint: a tag, then a 16-byte union (the coordinate).</summary>
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int Tag; public int Pad; public PointCoordinate Coord; public ulong Pad2; }
        [StructLayout(LayoutKind.Sequential)] public struct GridRef { public IntPtr Node; public ushort X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Selection { public GridRef Start, End; [MarshalAs(UnmanagedType.U1)] public bool Rectangle; }
        [StructLayout(LayoutKind.Sequential)] public struct SurfacePosition { public double X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct GestureGeometry { public uint Columns, CellWidth, PaddingLeft, ScreenHeight; }
        [StructLayout(LayoutKind.Sequential)] public struct MousePosition { public float X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MouseEncoderSize
        {
            public nuint Size;
            public uint ScreenWidth, ScreenHeight, CellWidth, CellHeight, PaddingTop, PaddingBottom, PaddingRight, PaddingLeft;
        }

        // ---- terminal
        [DllImport(Dll)] public static extern int ghostty_terminal_new(IntPtr allocator, out IntPtr terminal, ushort cols, ushort rows);
        [DllImport(Dll)] public static extern void ghostty_terminal_free(IntPtr terminal);
        [DllImport(Dll)] public static extern int ghostty_terminal_resize(IntPtr terminal, ushort cols, ushort rows, uint cellW, uint cellH);
        [DllImport(Dll)] public static extern int ghostty_terminal_set(IntPtr terminal, int option, void* value);
        [DllImport(Dll)] public static extern int ghostty_terminal_get(IntPtr terminal, int data, void* outValue);
        [DllImport(Dll)] public static extern void ghostty_terminal_vt_write(IntPtr terminal, byte* data, nuint len);
        [DllImport(Dll)] public static extern int ghostty_terminal_scroll_viewport(IntPtr terminal, ScrollViewport behaviour);
        [DllImport(Dll)] public static extern int ghostty_terminal_grid_ref(IntPtr terminal, Point point, GridRef* outRef);

        // ---- render state
        [DllImport(Dll)] public static extern int ghostty_render_state_new(IntPtr allocator, out IntPtr state);
        [DllImport(Dll)] public static extern void ghostty_render_state_free(IntPtr state);
        [DllImport(Dll)] public static extern int ghostty_render_state_update(IntPtr state, IntPtr terminal);
        [DllImport(Dll)] public static extern int ghostty_render_state_get(IntPtr state, int data, void* outValue);
        [DllImport(Dll)] public static extern int ghostty_render_state_set(IntPtr state, int option, void* value);
        [DllImport(Dll)] public static extern int ghostty_render_state_row_iterator_new(IntPtr allocator, out IntPtr iterator);
        [DllImport(Dll)] public static extern void ghostty_render_state_row_iterator_free(IntPtr iterator);
        [DllImport(Dll)][return: MarshalAs(UnmanagedType.U1)] public static extern bool ghostty_render_state_row_iterator_next(IntPtr iterator);
        [DllImport(Dll)] public static extern int ghostty_render_state_row_get(IntPtr iterator, int data, void* outValue);
        [DllImport(Dll)] public static extern int ghostty_render_state_row_set(IntPtr iterator, int option, void* value);
        [DllImport(Dll)] public static extern int ghostty_render_state_row_cells_new(IntPtr allocator, out IntPtr cells);
        [DllImport(Dll)] public static extern void ghostty_render_state_row_cells_free(IntPtr cells);
        [DllImport(Dll)][return: MarshalAs(UnmanagedType.U1)] public static extern bool ghostty_render_state_row_cells_next(IntPtr cells);
        [DllImport(Dll)] public static extern int ghostty_render_state_row_cells_get(IntPtr cells, int data, void* outValue);

        // ---- keys
        [DllImport(Dll)] public static extern int ghostty_key_encoder_new(IntPtr allocator, out IntPtr encoder);
        [DllImport(Dll)] public static extern void ghostty_key_encoder_free(IntPtr encoder);
        [DllImport(Dll)] public static extern int ghostty_key_encoder_set_from_terminal(IntPtr encoder, IntPtr terminal);
        [DllImport(Dll)] public static extern int ghostty_key_encoder_encode(IntPtr encoder, IntPtr keyEvent, byte* buf, nuint len, nuint* written);
        [DllImport(Dll)] public static extern int ghostty_key_event_new(IntPtr allocator, out IntPtr keyEvent);
        [DllImport(Dll)] public static extern void ghostty_key_event_free(IntPtr keyEvent);
        [DllImport(Dll)] public static extern int ghostty_key_event_set_action(IntPtr keyEvent, int action);
        [DllImport(Dll)] public static extern int ghostty_key_event_set_key(IntPtr keyEvent, int key);
        [DllImport(Dll)] public static extern int ghostty_key_event_set_mods(IntPtr keyEvent, ushort mods);
        [DllImport(Dll)] public static extern int ghostty_key_event_set_consumed_mods(IntPtr keyEvent, ushort mods);
        [DllImport(Dll)] public static extern int ghostty_key_event_set_utf8(IntPtr keyEvent, GString utf8);
        [DllImport(Dll)] public static extern int ghostty_key_event_set_unshifted_codepoint(IntPtr keyEvent, uint codepoint);

        // ---- mouse
        [DllImport(Dll)] public static extern int ghostty_mouse_encoder_new(IntPtr allocator, out IntPtr encoder);
        [DllImport(Dll)] public static extern void ghostty_mouse_encoder_free(IntPtr encoder);
        [DllImport(Dll)] public static extern int ghostty_mouse_encoder_set(IntPtr encoder, int option, void* value);
        [DllImport(Dll)] public static extern int ghostty_mouse_encoder_set_from_terminal(IntPtr encoder, IntPtr terminal);
        [DllImport(Dll)] public static extern int ghostty_mouse_encoder_encode(IntPtr encoder, IntPtr mouseEvent, byte* buf, nuint len, nuint* written);
        [DllImport(Dll)] public static extern int ghostty_mouse_event_new(IntPtr allocator, out IntPtr mouseEvent);
        [DllImport(Dll)] public static extern void ghostty_mouse_event_free(IntPtr mouseEvent);
        [DllImport(Dll)] public static extern int ghostty_mouse_event_set_action(IntPtr mouseEvent, int action);
        [DllImport(Dll)] public static extern int ghostty_mouse_event_set_button(IntPtr mouseEvent, int button);
        [DllImport(Dll)] public static extern int ghostty_mouse_event_clear_button(IntPtr mouseEvent);
        [DllImport(Dll)] public static extern int ghostty_mouse_event_set_mods(IntPtr mouseEvent, ushort mods);
        [DllImport(Dll)] public static extern int ghostty_mouse_event_set_position(IntPtr mouseEvent, MousePosition position);

        // ---- selection
        [DllImport(Dll)] public static extern int ghostty_selection_gesture_new(IntPtr allocator, out IntPtr gesture);
        [DllImport(Dll)] public static extern void ghostty_selection_gesture_free(IntPtr gesture, IntPtr terminal);
        [DllImport(Dll)] public static extern void ghostty_selection_gesture_reset(IntPtr gesture, IntPtr terminal);
        [DllImport(Dll)] public static extern int ghostty_selection_gesture_get(IntPtr gesture, IntPtr terminal, int data, void* value);
        [DllImport(Dll)] public static extern int ghostty_selection_gesture_event_new(IntPtr allocator, out IntPtr gestureEvent, int type);
        [DllImport(Dll)] public static extern void ghostty_selection_gesture_event_free(IntPtr gestureEvent);
        [DllImport(Dll)] public static extern int ghostty_selection_gesture_event_set(IntPtr gestureEvent, int option, void* value);
        [DllImport(Dll)] public static extern int ghostty_selection_gesture_event(IntPtr gesture, IntPtr terminal, IntPtr gestureEvent, Selection* outSelection);
        [DllImport(Dll)] public static extern int ghostty_terminal_select_all(IntPtr terminal, Selection* outSelection);

        // ---- formatter (the text of a selection, or of the screen)
        [DllImport(Dll)] public static extern int ghostty_formatter_terminal_new(IntPtr allocator, out IntPtr formatter, IntPtr terminal);
        [DllImport(Dll)] public static extern int ghostty_formatter_set(IntPtr formatter, int option, void* value);
        [DllImport(Dll)] public static extern int ghostty_formatter_format_alloc(IntPtr formatter, IntPtr allocator, byte** outPtr, nuint* outLen);
        [DllImport(Dll)] public static extern void ghostty_formatter_free(IntPtr formatter);
        [DllImport(Dll)] public static extern void ghostty_free(IntPtr allocator, byte* ptr, nuint len);

        // ---- colours
        [DllImport(Dll)] public static extern void ghostty_color_palette_default(Rgb* out256);

        // ---- paste, focus
        [DllImport(Dll)][return: MarshalAs(UnmanagedType.U1)] public static extern bool ghostty_paste_is_safe(byte* data, nuint len);
        [DllImport(Dll)] public static extern int ghostty_paste_encode(byte* data, nuint dataLen, [MarshalAs(UnmanagedType.U1)] bool bracketed, byte* buf, nuint bufLen, nuint* written);
        [DllImport(Dll)] public static extern int ghostty_focus_encode(int focusEvent, byte* buf, nuint bufLen, nuint* written);

        /// <summary>A DEC private mode's state (false when it can't be read).</summary>
        public static bool Mode(IntPtr terminal, ushort mode)
        {
            var c = new ModeConfig { Mode = mode };
            return ghostty_terminal_get(terminal, DATA_MODE, &c) == SUCCESS && c.Value;
        }

        /// <summary>Loads the DLL now: false (with the reason) when it can't be.</summary>
        public static bool TryLoad(out string error)
        {
            error = null;
            try
            {
                IntPtr t;
                if (ghostty_terminal_new(IntPtr.Zero, out t, 10, 2) != SUCCESS) { error = "ghostty_terminal_new failed"; return false; }
                ghostty_terminal_free(t);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
