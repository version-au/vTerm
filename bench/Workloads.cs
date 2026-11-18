using System;
using System.Collections.Generic;
using System.Text;

namespace GhosttyBench
{
    /// <summary>The same output for both terminals, made from a fixed seed so every run is alike.</summary>
    sealed class Workload
    {
        public string Name, About;
        public string Text;
        public byte[] Bytes;
    }

    static class Workloads
    {
        static readonly string[] Words =
        {
            "error", "info", "warn", "debug", "request", "GET", "/api/v1/changes", "200", "404", "container", "started", "stopped",
            "proxmox", "docker", "compose", "pulled", "image", "sha256:3f9a1c", "node", "build", "release", "vTerm", "the", "a",
            "and", "user", "config", "settings.json", "update", "restart", "service", "nginx", "postgres", "redis", "12ms", "0.38.227",
        };
        static readonly string[] Wide = { "日本語", "中文字符", "한국어", "テスト", "表示", "端末", "😀", "🚀", "✅", "🔥" };
        static readonly string[] Accents = { "café", "naïve", "Zürich", "São Paulo", "Ærøskøbing", "façade", "Ελληνικά", "Русский" };
        static readonly string Box = "─│┌┐└┘├┤┬┴┼═║╔╗╚╝";

        public static List<Workload> All(int megabytes, int cols, int rows)
        {
            int target = megabytes * 1024 * 1024;
            var list = new List<Workload>
            {
                Make("plain", "plain ASCII log lines (cat of a big log)", target, Plain),
                Make("colour", "a colour change every few words (ls --color, coloured logs, git diff)", target, Colour),
                Make("unicode", "accents, CJK, emoji and box drawing (wide characters)", target, Unicode),
                Make("fullscreen", "a full-screen app redrawing (htop / vim / Claude Code): cursor moves + colours", target, (sb, r) => FullScreen(sb, r, cols, rows)),
            };
            return list;
        }

        static Workload Make(string name, string about, int target, Action<StringBuilder, Random> line)
        {
            var r = new Random(42);
            var sb = new StringBuilder(target + 4096);
            while (sb.Length < target) line(sb, r);
            string text = sb.ToString();
            return new Workload { Name = name, About = about, Text = text, Bytes = Encoding.UTF8.GetBytes(text) };
        }

        static void Plain(StringBuilder sb, Random r)
        {
            sb.Append("2026-11-18 14:").Append(r.Next(10, 60)).Append(':').Append(r.Next(10, 60)).Append(' ');
            int len = r.Next(40, 115);
            int start = sb.Length;
            while (sb.Length - start < len) sb.Append(Words[r.Next(Words.Length)]).Append(' ');
            sb.Append("\r\n");
        }

        static void Colour(StringBuilder sb, Random r)
        {
            int n = r.Next(6, 16);
            for (int i = 0; i < n; i++)
            {
                switch (r.Next(4))
                {
                    case 0: sb.Append("\x1b[").Append(31 + r.Next(7)).Append('m'); break;
                    case 1: sb.Append("\x1b[1;38;5;").Append(r.Next(256)).Append('m'); break;
                    case 2: sb.Append("\x1b[38;2;").Append(r.Next(256)).Append(';').Append(r.Next(256)).Append(';').Append(r.Next(256)).Append('m'); break;
                    default: sb.Append("\x1b[0m"); break;
                }
                sb.Append(Words[r.Next(Words.Length)]).Append(' ');
            }
            sb.Append("\x1b[0m\r\n");
        }

        static void Unicode(StringBuilder sb, Random r)
        {
            int n = r.Next(6, 14);
            for (int i = 0; i < n; i++)
            {
                switch (r.Next(4))
                {
                    case 0: sb.Append(Wide[r.Next(Wide.Length)]); break;
                    case 1: sb.Append(Accents[r.Next(Accents.Length)]); break;
                    case 2: for (int k = r.Next(3, 10); k > 0; k--) sb.Append(Box[r.Next(Box.Length)]); break;
                    default: sb.Append(Words[r.Next(Words.Length)]); break;
                }
                sb.Append(' ');
            }
            sb.Append("\r\n");
        }

        /// <summary>One whole screen of an htop-like app: home, then each row placed and drawn with colours, in the alternate screen.</summary>
        static void FullScreen(StringBuilder sb, Random r, int cols, int rows)
        {
            if (sb.Length == 0) sb.Append("\x1b[?1049h\x1b[?25l");
            sb.Append("\x1b[H");
            for (int y = 1; y <= rows; y++)
            {
                sb.Append("\x1b[").Append(y).Append(";1H");
                int x = 0;
                while (x < cols - 12)
                {
                    sb.Append(r.Next(3) == 0 ? "\x1b[42;30m" : "\x1b[0;3" + (1 + r.Next(6)) + "m");
                    string w = Words[r.Next(Words.Length)];
                    if (x + w.Length + 1 > cols) break;
                    sb.Append(w).Append(' ');
                    x += w.Length + 1;
                }
                sb.Append("\x1b[0m\x1b[K");
            }
        }
    }
}
