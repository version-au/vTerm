using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace vTerm.Core
{
    /// <summary>
    /// A shell (PowerShell, cmd, WSL ...) running in a Windows pseudo console (ConPTY) - the same thing Windows Terminal uses. What the
    /// shell writes comes out as text with VT escape codes (colours, cursor moves) for a terminal display to draw; what is typed goes in as
    /// text. Output arrives on a background thread.
    /// </summary>
    public sealed class ConPty : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct COORD { public short X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern void ClosePseudoConsole(IntPtr hPC);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
                                          bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
                                          ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr h, uint code);

        const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        const int STARTF_USESTDHANDLES = 0x00000100;
        static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        IntPtr _pc, _attrList, _process, _thread;
        FileStream _in, _out;
        Thread _reader, _waiter;
        readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
        readonly object _writeLock = new object();
        int _disposed;

        /// <summary>Text the shell wrote (VT codes included). On a background thread.</summary>
        public event Action<string> Output;
        /// <summary>The shell ended (its exit code). On a background thread.</summary>
        public event Action<int> Exited;

        public int ProcessId { get; private set; }
        public bool HasExited { get; private set; }

        /// <summary>Start commandLine (e.g. "powershell.exe -NoLogo") in a pseudo console of cols x rows, in folder dir (null = the user's folder).</summary>
        public void Start(string commandLine, string dir, int cols, int rows) { Start(commandLine, dir, cols, rows, null); }

        /// <summary>The same, with extra environment variables for the shell (added to vTerm's own).</summary>
        public void Start(string commandLine, string dir, int cols, int rows, IDictionary<string, string> extraEnv)
        {
            SafeFileHandle inRead, inWrite, outRead, outWrite;
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe");
            if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe");
            var size = new COORD { X = (short)Math.Max(2, Math.Min(500, cols)), Y = (short)Math.Max(1, Math.Min(300, rows)) };
            int hr = CreatePseudoConsole(size, inRead, outWrite, 0, out _pc);
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole (needs Windows 10 1809 or newer)");
            // the pseudo console holds its own copies of these
            inRead.Dispose();
            outWrite.Dispose();

            // the attribute list that hands the pseudo console to the new process
            IntPtr bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            _attrList = Marshal.AllocHGlobal(bytes);
            if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(_attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;     // no handles of ours leak in: the pseudo console is its console
            si.lpAttributeList = _attrList;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            PROCESS_INFORMATION pi;
            IntPtr envBlock = IntPtr.Zero;
            if (extraEnv != null && extraEnv.Count > 0)
            {
                // vTerm's environment plus the extras, as a sorted "name=value\0...\0\0" block
                var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables()) all[(string)de.Key] = (string)de.Value;
                foreach (var kv in extraEnv) all[kv.Key] = kv.Value;
                var block = new StringBuilder();
                foreach (var kv in all) block.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
                block.Append('\0');
                envBlock = Marshal.StringToHGlobalUni(block.ToString());
            }
            bool ok;
            try
            {
                ok = CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                                    envBlock, dir, ref si, out pi);
            }
            finally { if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock); }
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                inWrite.Dispose(); outRead.Dispose();
                throw new Win32Exception(err, "Couldn't start \"" + commandLine + "\"");
            }
            _process = pi.hProcess;
            _thread = pi.hThread;
            ProcessId = pi.dwProcessId;

            _in = new FileStream(inWrite, FileAccess.Write, 1, false);
            _out = new FileStream(outRead, FileAccess.Read, 1, false);
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPty read" };
            _reader.Start();
            _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "ConPty wait" };
            _waiter.Start();
        }

        void ReadLoop()
        {
            var buf = new byte[16384];
            var chars = new char[16384 + 8];
            try
            {
                while (true)
                {
                    int n = _out.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    int c = _utf8.GetChars(buf, 0, n, chars, 0, false);     // keeps a character split across two reads
                    if (c > 0)
                    {
                        Action<string> h = Output;
                        if (h != null) h(new string(chars, 0, c));
                    }
                }
            }
            catch (Exception) { }
        }

        void WaitLoop()
        {
            try
            {
                WaitForSingleObject(_process, 0xFFFFFFFF);
                uint code;
                if (!GetExitCodeProcess(_process, out code)) code = 0;
                HasExited = true;
                Action<int> h = Exited;
                if (h != null) h(unchecked((int)code));
            }
            catch (Exception) { }
        }

        /// <summary>Type text into the shell (keys arrive as text / VT sequences from the terminal display).</summary>
        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text) || _in == null || _disposed != 0) return;
            byte[] b = Encoding.UTF8.GetBytes(text);
            try { lock (_writeLock) { _in.Write(b, 0, b.Length); _in.Flush(); } }
            catch (Exception) { }
        }

        public void Resize(int cols, int rows)
        {
            if (_pc == IntPtr.Zero || _disposed != 0) return;
            try { ResizePseudoConsole(_pc, new COORD { X = (short)Math.Max(2, Math.Min(500, cols)), Y = (short)Math.Max(1, Math.Min(300, rows)) }); }
            catch (Exception) { }
        }

        /// <summary>End it: close the pseudo console (the shell gets the close signal), then make sure the process is gone.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            IntPtr pc = _pc, proc = _process, thread = _thread, attr = _attrList;
            _pc = IntPtr.Zero;
            // ClosePseudoConsole can wait for the output to be drained (the reader keeps reading), so not on the caller's thread
            var t = new Thread(delegate ()
            {
                try { if (pc != IntPtr.Zero) ClosePseudoConsole(pc); } catch { }
                try { if (proc != IntPtr.Zero && WaitForSingleObject(proc, 3000) != 0) TerminateProcess(proc, 1); } catch { }
                try { if (_in != null) _in.Dispose(); } catch { }
                try { if (_out != null) _out.Dispose(); } catch { }
                try { if (proc != IntPtr.Zero) CloseHandle(proc); } catch { }
                try { if (thread != IntPtr.Zero) CloseHandle(thread); } catch { }
                try { if (attr != IntPtr.Zero) { DeleteProcThreadAttributeList(attr); Marshal.FreeHGlobal(attr); } } catch { }
            }) { IsBackground = true, Name = "ConPty close" };
            t.Start();
        }
    }
}
