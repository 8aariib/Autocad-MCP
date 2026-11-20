using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCADChat
{
    /// <summary>User settings, kept in %APPDATA%\AutoCADChat\settings.json.
    /// The API key is encrypted with Windows DPAPI for the current user only.</summary>
    public sealed class AppSettings
    {
        public static readonly string[] Models = { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" };
        public static readonly string[] Efforts = { "low", "medium", "high", "xhigh" };

        /// <summary>"claude-code": the user's Claude account through Claude Code (no API key).
        /// "api": an Anthropic API key.</summary>
        public string Engine { get; set; } = EngineClaudeCode;
        public const string EngineClaudeCode = "claude-code";
        public const string EngineApi = "api";

        public string Model { get; set; } = "claude-opus-5-5";
        public string Effort { get; set; } = "medium";
        public bool ConfirmDestructive { get; set; } = true;
        public bool ReadOnly { get; set; } = false;
        /// <summary>Folders the tools may open and save drawings in, separated by ';'.</summary>
        public string AllowedFolders { get; set; } =
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        /// <summary>Optional full path to uv.exe; found automatically when empty.</summary>
        public string UvPath { get; set; } = "";
        public bool OpenOnStartup { get; set; } = false;
        public string ProtectedApiKey { get; set; } = "";

        [JsonIgnore]
        public string ApiKey
        {
            get
            {
                if (string.IsNullOrEmpty(ProtectedApiKey))
                    return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
                try
                {
                    var bytes = Dpapi.Unprotect(Convert.FromBase64String(ProtectedApiKey), Entropy);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch (Exception)
                {
                    return "";
                }
            }
            set
            {
                var key = (value ?? "").Trim();
                ProtectedApiKey = key.Length == 0
                    ? ""
                    : Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(key), Entropy));
            }
        }

        [JsonIgnore] public bool HasApiKey => ApiKey.Length > 0;
        [JsonIgnore] public bool UsesApiKey => Engine == EngineApi;

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AutoCADChat/v1");

        private static string FilePath => Path.Combine(Paths.ConfigDir, "settings.json");

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                    if (s != null)
                    {
                        if (Array.IndexOf(Models, s.Model) < 0) s.Model = Models[0];
                        if (Array.IndexOf(Efforts, s.Effort) < 0) s.Effort = "medium";
                        if (s.Engine != EngineApi) s.Engine = EngineClaudeCode;
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("settings could not be read, using defaults: " + ex.Message);
            }
            return new AppSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Paths.ConfigDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }

        public AppSettings Clone() => (AppSettings)MemberwiseClone();

        /// <summary>True when a change needs the tool server restarted (it reads these at start).</summary>
        public bool NeedsRestartComparedTo(AppSettings other) =>
            Engine != other.Engine || ReadOnly != other.ReadOnly || AllowedFolders != other.AllowedFolders ||
            UvPath != other.UvPath || ProtectedApiKey != other.ProtectedApiKey;
    }
}
