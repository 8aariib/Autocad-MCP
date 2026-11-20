using System;
using System.IO;
using System.Reflection;

namespace AutoCADChat
{
    /// <summary>Where things live on disk.</summary>
    public static class Paths
    {
        public static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoCADChat");

        public static string LocalDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCADChat");

        /// <summary>The Python environment uv creates for the bridge (outside the bundle, so updates are clean).</summary>
        public static string VenvDir => Path.Combine(LocalDir, "venv");

        public static string LogDir => Path.Combine(LocalDir, "logs");

        /// <summary>The folder holding AutoCADChat.dll: Contents\ of the bundle.</summary>
        public static string ContentsDir =>
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;

        /// <summary>The bridge's Python project (pyproject.toml + uv.lock), shipped in the bundle.</summary>
        public static string BridgeDir => Path.Combine(ContentsDir, "bridge");

        /// <summary>uv.exe: the configured path, then the usual install places, then PATH.</summary>
        public static string? FindUv(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates =
            {
                Path.Combine(home, ".local", "bin", "uv.exe"),
                Path.Combine(home, ".cargo", "bin", "uv.exe"),
                Path.Combine(local, "Microsoft", "WinGet", "Links", "uv.exe"),
                Path.Combine(local, "Programs", "uv", "uv.exe"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var c = Path.Combine(dir.Trim(), "uv.exe");
                    if (File.Exists(c)) return c;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry
                }
            }
            return null;
        }
    }

    /// <summary>A small rolling log in %LOCALAPPDATA%\AutoCADChat\logs.</summary>
    public static class Log
    {
        private static readonly object Gate = new();
        public static string FilePath => Path.Combine(Paths.LogDir, "panel.log");

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.LogDir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 2_000_000)
                        File.Move(FilePath, FilePath + ".old", true);
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
                }
            }
            catch (Exception)
            {
                // logging must never take the panel down
            }
        }
    }
}
