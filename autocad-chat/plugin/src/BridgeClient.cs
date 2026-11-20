using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AutoCADChat
{
    /// <summary>Runs the Python bridge (through uv) and speaks its JSON-lines protocol.
    /// Every event is raised on the panel's UI thread; nothing here ever blocks it.</summary>
    public sealed class BridgeClient : IDisposable
    {
        private readonly Dispatcher _ui;
        private Process? _proc;
        private StreamWriter? _stdin;
        private JobObject? _job;
        private readonly object _gate = new();
        private int _generation;

        public event Action<JsonElement>? EventReceived;
        public event Action<string>? SetupProgress;
        public event Action<int, string>? Exited;

        public bool IsRunning { get { lock (_gate) return _proc is { HasExited: false }; } }

        public BridgeClient(Dispatcher ui) => _ui = ui;

        /// <summary>Starts the bridge. Returns null on success, or a message for the user.</summary>
        public string? Start(AppSettings s)
        {
            Stop();
            var uv = Paths.FindUv(s.UvPath);
            if (uv == null)
                return "uv is not installed. Run install.ps1 from the Claude for AutoCAD folder, or set the uv path in Settings.";
            if (!File.Exists(Path.Combine(Paths.BridgeDir, "pyproject.toml")))
                return "The bridge files are missing from " + Paths.BridgeDir + ". Reinstall Claude for AutoCAD.";
            if (s.UsesApiKey && !s.HasApiKey)
                return "Add your Anthropic API key in Settings, or switch Sign in with to your Claude account.";

            var psi = new ProcessStartInfo
            {
                FileName = uv,
                WorkingDirectory = Paths.LocalDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var a in new[] { "run", "--project", Paths.BridgeDir, "--frozen", "--no-dev", "autocad-chat-bridge" })
                psi.ArgumentList.Add(a);

            var env = psi.Environment;
            env["UV_PROJECT_ENVIRONMENT"] = Paths.VenvDir;
            env["UV_NO_PROGRESS"] = "1";
            env["PYTHONUTF8"] = "1";
            env["PYTHONUNBUFFERED"] = "1";
            env["ACADCHAT_ENGINE"] = s.Engine;
            if (s.UsesApiKey) env["ANTHROPIC_API_KEY"] = s.ApiKey;
            else env.Remove("ANTHROPIC_API_KEY");
            env.Remove("ANTHROPIC_AUTH_TOKEN");
            var claude = Paths.FindClaude();
            if (claude != null)
                env["ACADCHAT_CLAUDE_CMD"] = System.Text.Json.JsonSerializer.Serialize(
                    claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        ? new[] { Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", "/c", claude }
                        : new[] { claude });
            env["ACADCHAT_MODEL"] = s.Model;
            env["ACADCHAT_EFFORT"] = s.Effort;
            env["ACADCHAT_CONFIRM"] = s.ConfirmDestructive ? "1" : "0";
            env["ACADCHAT_READ_ONLY"] = s.ReadOnly ? "1" : "0";
            // The server separates folders with commas.
            env["ACADCHAT_ALLOWED_PATHS"] = string.Join(",",
                s.AllowedFolders.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            Directory.CreateDirectory(Paths.LocalDir);
            Directory.CreateDirectory(Paths.LogDir);
            Process proc;
            try
            {
                proc = Process.Start(psi) ?? throw new InvalidOperationException("the process did not start");
            }
            catch (Exception ex)
            {
                Log.Write("bridge start failed: " + ex);
                return "Could not start the bridge: " + ex.Message;
            }

            int gen;
            lock (_gate)
            {
                _proc = proc;
                gen = ++_generation;
                _stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                try
                {
                    _job ??= new JobObject();
                    _job.Add(proc);
                }
                catch (Exception ex)
                {
                    Log.Write("job object unavailable: " + ex.Message);
                }
            }
            Log.Write($"bridge started (pid {proc.Id}) with {uv}");
            _ = Task.Run(() => PumpStdout(proc, gen));
            _ = Task.Run(() => PumpStderr(proc, gen));
            return null;
        }

        public void Send(object message)
        {
            var line = JsonSerializer.Serialize(message);
            StreamWriter? w;
            lock (_gate) w = _stdin;
            if (w == null) return;
            // Writes are tiny; do them off the UI thread anyway so a stuck pipe can never freeze AutoCAD.
            Task.Run(() =>
            {
                try
                {
                    lock (w) w.WriteLine(line);
                }
                catch (Exception ex)
                {
                    Log.Write("bridge write failed: " + ex.Message);
                }
            });
        }

        public void Stop()
        {
            Process? p;
            StreamWriter? w;
            lock (_gate)
            {
                p = _proc;
                w = _stdin;
                _proc = null;
                _stdin = null;
                _generation++;
            }
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    if (w != null)
                    {
                        try { lock (w) w.WriteLine("{\"type\":\"shutdown\"}"); } catch (Exception) { }
                    }
                    if (!p.WaitForExit(1500)) p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Log.Write("bridge stop: " + ex.Message);
            }
            finally
            {
                p.Dispose();
            }
        }

        private void PumpStdout(Process p, int gen)
        {
            try
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    JsonElement ev;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        ev = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        Log.Write("bridge (non-JSON stdout): " + line);
                        continue;
                    }
                    _ui.BeginInvoke(() => { if (gen == _generation) EventReceived?.Invoke(ev); });
                }
            }
            catch (Exception ex)
            {
                Log.Write("bridge stdout: " + ex.Message);
            }
            int code = -1;
            try { p.WaitForExit(3000); code = p.HasExited ? p.ExitCode : -1; } catch (Exception) { }
            string tail = LastErrorLines();
            _ui.BeginInvoke(() => { if (gen == _generation) Exited?.Invoke(code, tail); });
        }

        private readonly Queue<string> _recentErr = new();

        private string LastErrorLines()
        {
            lock (_recentErr) return string.Join("\n", _recentErr);
        }

        private void PumpStderr(Process p, int gen)
        {
            var path = Path.Combine(Paths.LogDir, "bridge.log");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 5_000_000) File.Move(path, path + ".old", true);
                using var log = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
                log.WriteLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} bridge start ----");
                string? line;
                while ((line = p.StandardError.ReadLine()) != null)
                {
                    log.WriteLine(line);
                    lock (_recentErr)
                    {
                        _recentErr.Enqueue(line);
                        while (_recentErr.Count > 8) _recentErr.Dequeue();
                    }
                    var t = line.TrimStart();
                    // uv's first-run setup lines, shown so a long first start is not a mystery.
                    if (t.StartsWith("Downloading") || t.StartsWith("Installed") || t.StartsWith("Prepared") ||
                        t.StartsWith("Using CPython") || t.StartsWith("Creating virtual environment") || t.StartsWith("Resolved"))
                        _ui.BeginInvoke(() => { if (gen == _generation) SetupProgress?.Invoke(t); });
                }
            }
            catch (Exception ex)
            {
                Log.Write("bridge stderr: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Stop();
            _job?.Dispose();
            _job = null;
        }
    }
}
