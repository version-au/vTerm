using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GhosttyBench
{
    /// <summary>vTerm's display as it is: xterm.js (vTerm's copy) in WebView2, fed by PostWebMessageAsJson.</summary>
    sealed class XtermHost
    {
        public Window Window;
        public WebView2 Web;
        public CoreWebView2Environment Env;
        readonly List<KeyValuePair<Func<JsonElement, bool>, TaskCompletionSource<JsonElement>>> _waits = new List<KeyValuePair<Func<JsonElement, bool>, TaskCompletionSource<JsonElement>>>();

        /// <summary>Start WebView2 and load the page; returns the time to the page's "ready".</summary>
        public async Task<double> StartAsync(string profileDir, string vtermAssets, string webDir, string dataDir, double w, double h)
        {
            var sw = Stopwatch.StartNew();
            Window = new Window { Title = "xterm.js (vTerm's display)", Width = w, Height = h, Left = 40, Top = 40, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
            Web = new WebView2();
            Window.Content = Web;
            Window.Show();
            Env = await CoreWebView2Environment.CreateAsync(null, profileDir);
            await Web.EnsureCoreWebView2Async(Env);
            CoreWebView2 core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.SetVirtualHostNameToFolderMapping("vterm.local", vtermAssets, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("bench.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("data.local", dataDir, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnMessage;
            Task<JsonElement> ready = Wait(m => Str(m, "t") == "ready");
            core.Navigate("https://bench.local/bench.html");
            await ready;
            return sw.Elapsed.TotalMilliseconds;
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonElement m = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone();
            for (int i = 0; i < _waits.Count; i++)
            {
                if (!_waits[i].Key(m)) continue;
                TaskCompletionSource<JsonElement> t = _waits[i].Value;
                _waits.RemoveAt(i);
                t.TrySetResult(m);
                return;
            }
        }

        public Task<JsonElement> Wait(Func<JsonElement, bool> match)
        {
            var t = new TaskCompletionSource<JsonElement>();
            _waits.Add(new KeyValuePair<Func<JsonElement, bool>, TaskCompletionSource<JsonElement>>(match, t));
            return t.Task;
        }

        public static string Str(JsonElement m, string name) { JsonElement v; return m.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : ""; }

        public void Post(object msg) { Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); }

        public async Task NewTerm(string id, int cols, int rows, bool off)
        {
            Task<JsonElement> made = Wait(m => Str(m, "t") == "made" && Str(m, "id") == id);
            Post(new { t = "new", id = id, cols = cols, rows = rows, off = off });
            await made;
        }

        public async Task Close(string id)
        {
            Task<JsonElement> done = Wait(m => Str(m, "t") == "closed" && Str(m, "id") == id);
            Post(new { t = "close", id = id });
            await done;
        }

        public async Task Reset(string id)
        {
            Task<JsonElement> done = Wait(m => Str(m, "t") == "wasreset" && Str(m, "id") == id);
            Post(new { t = "reset", id = id });
            await done;
        }

        /// <summary>Everything posted so far is parsed and drawn.</summary>
        public async Task Sync(string id)
        {
            Task<JsonElement> done = Wait(m => Str(m, "t") == "synced" && Str(m, "id") == id);
            Post(new { t = "sync", id = id });
            await done;
        }

        /// <summary>A workload file written inside the page as fast as xterm.js takes it: (parsed ms, parsed + drawn ms).</summary>
        public async Task<Tuple<double, double>> Run(string id, string file)
        {
            Task<JsonElement> done = Wait(m => Str(m, "t") == "ran" && Str(m, "id") == id);
            Post(new { t = "run", id = id, url = "https://data.local/" + Path.GetFileName(file), chunk = 128 * 1024 });
            JsonElement r = await done;
            return Tuple.Create(r.GetProperty("ms").GetDouble(), r.GetProperty("drawnMs").GetDouble());
        }

        /// <summary>
        /// vTerm's own path (TerminalView.FlushOutput): output waits in a StringBuilder, a 15 ms Background DispatcherTimer posts up to
        /// 128K characters a tick as JSON. feed (any thread) adds output; the task ends when the feed is done and everything is drawn.
        /// </summary>
        public async Task<double> PumpLikeVterm(string id, Action<Action<string>> feed)
        {
            var pending = new System.Text.StringBuilder();
            bool fed = false;
            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
            var drained = new TaskCompletionSource<bool>();
            timer.Tick += delegate
            {
                string text = null;
                lock (pending)
                {
                    if (pending.Length > 0)
                    {
                        int take = Math.Min(pending.Length, 128 * 1024);
                        if (take < pending.Length && char.IsHighSurrogate(pending[take - 1])) take--;
                        text = pending.ToString(0, take);
                        pending.Remove(0, take);
                    }
                    else if (fed) { timer.Stop(); drained.TrySetResult(true); }
                }
                if (text != null) Post(new { t = "o", id = id, d = text });
            };
            timer.Start();
            await Task.Run(delegate
            {
                feed(delegate (string s)
                {
                    lock (pending) pending.Append(s);
                    // HoldBack: the shell waits while 2M characters are waiting to be shown
                    for (int i = 0; i < 400; i++) { int n; lock (pending) n = pending.Length; if (n < 2 * 1024 * 1024) break; System.Threading.Thread.Sleep(5); }
                });
            });
            lock (pending) fed = true;
            await drained.Task;
            await Sync(id);
            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Private bytes of every WebView2 process (browser, renderer, GPU, utility).</summary>
        public Task<long> ProcessBytes()
        {
            long sum = 0;
            IReadOnlyList<CoreWebView2ProcessInfo> list = Env.GetProcessInfos();
            foreach (CoreWebView2ProcessInfo p in list)
            {
                try { using (Process pr = Process.GetProcessById(p.ProcessId)) sum += pr.PrivateMemorySize64; } catch { }
            }
            return Task.FromResult(sum);
        }

        public void Close()
        {
            try { Web.Dispose(); } catch { }
            try { Window.Close(); } catch { }
        }
    }
}
