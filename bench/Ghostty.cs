using System;
using System.Runtime.InteropServices;

namespace GhosttyBench
{
    /// <summary>The parts of libghostty-vt 1.0's C API the bench uses (ghostty/vt/*.h).</summary>
    static unsafe class Gt
    {
        const string Dll = "ghostty-vt.dll";

        public const int SUCCESS = 0, NO_VALUE = -4;

        // GhosttyTerminalOption
        public const int OPT_SCROLLBACK_MAX_LINES = 29;
        // GhosttyTerminalData
        public const int DATA_MEMORY_USAGE = 42;
        // GhosttyRenderStateData
        public const int RS_COLS = 1, RS_ROWS = 2, RS_DIRTY = 3, RS_ROW_ITERATOR = 4, RS_COLOR_BACKGROUND = 5, RS_COLOR_FOREGROUND = 6,
                         RS_CURSOR_VISIBLE = 11, RS_CURSOR_VIEWPORT_HAS_VALUE = 14, RS_CURSOR_VIEWPORT_X = 15, RS_CURSOR_VIEWPORT_Y = 16;
        // GhosttyRenderStateOption
        public const int RS_OPT_DIRTY = 1;
        // GhosttyRenderStateDirty
        public const int DIRTY_FALSE = 0, DIRTY_PARTIAL = 1, DIRTY_FULL = 2;
        // GhosttyRenderStateRowData / Option
        public const int ROW_DIRTY = 1, ROW_CELLS = 3, ROW_OPT_DIRTY = 1;
        // GhosttyRenderStateRowCellsData
        public const int CELL_STYLE = 2, CELL_GRAPHEMES_LEN = 3, CELL_GRAPHEMES_BUF = 4, CELL_BG = 5, CELL_FG = 6, CELL_HAS_STYLING = 8;
        // GhosttyStyleFlags
        public const ushort FLAG_BOLD = 1, FLAG_ITALIC = 2, FLAG_FAINT = 4, FLAG_INVERSE = 16, FLAG_INVISIBLE = 32;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Rgb { public byte R, G, B; }

        /// <summary>GhosttyStyle: three 4-byte colours, then the flags (16 bytes).</summary>
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct Style { [FieldOffset(12)] public ushort Flags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CodepointBuffer { public uint* Ptr; public nuint Cap; public nuint Len; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryUsage
        {
            public nuint Size;
            public byte CompressionSupported;
            public ulong PrimaryPages, PrimaryVirtual, PrimaryResident, PrimaryCompressedPages, PrimaryCompressedBytes, PrimaryImage;
            public ulong AltPages, AltVirtual, AltResident, AltCompressedPages, AltCompressedBytes, AltImage;
        }

        [DllImport(Dll)] public static extern int ghostty_terminal_new(IntPtr allocator, out IntPtr terminal, ushort cols, ushort rows);
        [DllImport(Dll)] public static extern void ghostty_terminal_free(IntPtr terminal);
        [DllImport(Dll)] public static extern void ghostty_terminal_reset(IntPtr terminal);
        [DllImport(Dll)] public static extern int ghostty_terminal_resize(IntPtr terminal, ushort cols, ushort rows, uint cellW, uint cellH);
        [DllImport(Dll)] public static extern int ghostty_terminal_set(IntPtr terminal, int option, void* value);
        [DllImport(Dll)] public static extern int ghostty_terminal_get(IntPtr terminal, int data, void* outValue);
        [DllImport(Dll)] public static extern void ghostty_terminal_vt_write(IntPtr terminal, byte* data, nuint len);

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

        public static IntPtr NewTerminal(int cols, int rows, int scrollbackLines)
        {
            IntPtr t;
            int r = ghostty_terminal_new(IntPtr.Zero, out t, (ushort)cols, (ushort)rows);
            if (r != SUCCESS) throw new InvalidOperationException("ghostty_terminal_new: " + r);
            nuint lines = (nuint)scrollbackLines;
            ghostty_terminal_set(t, OPT_SCROLLBACK_MAX_LINES, &lines);
            return t;
        }

        public static void Write(IntPtr term, byte[] data, int offset, int count)
        {
            fixed (byte* p = data) ghostty_terminal_vt_write(term, p + offset, (nuint)count);
        }

        public static MemoryUsage Memory(IntPtr term)
        {
            var m = new MemoryUsage { Size = (nuint)sizeof(MemoryUsage) };
            int r = ghostty_terminal_get(term, DATA_MEMORY_USAGE, &m);
            if (r != SUCCESS) throw new InvalidOperationException("memory usage: " + r);
            return m;
        }
    }
}
