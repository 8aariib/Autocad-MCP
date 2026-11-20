using System;
using System.Drawing;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AutoCADChat.Plugin))]
[assembly: CommandClass(typeof(AutoCADChat.Commands))]

namespace AutoCADChat
{
    /// <summary>Loaded by AutoCAD from the bundle. Owns the one palette and its chat.</summary>
    public sealed class Plugin : IExtensionApplication
    {
        private static PaletteSet? _palette;
        private static ChatView? _view;
        private static readonly Guid PaletteId = new("6f1d9b52-3c1e-4f0b-9a53-2a7d1c0e8b41");

        public void Initialize()
        {
            Log.Write($"loaded into AutoCAD {AcApp.Version}");
            AcApp.QuitWillStart += (_, _) => Shutdown();
            try
            {
                var settings = AppSettings.Load();
                if (settings.OpenOnStartup)
                    AcApp.Idle += OpenOnceIdle;
                WriteLine("\nClaude for AutoCAD loaded. Type CLAUDE to open the chat panel.\n");
            }
            catch (System.Exception ex)
            {
                Log.Write("initialize: " + ex);
            }
        }

        private static void OpenOnceIdle(object? sender, EventArgs e)
        {
            AcApp.Idle -= OpenOnceIdle;
            Show();
        }

        public void Terminate() => Shutdown();

        private static void Shutdown()
        {
            try
            {
                _view?.Shutdown();
            }
            catch (System.Exception ex)
            {
                Log.Write("shutdown: " + ex.Message);
            }
        }

        public static void Show()
        {
            try
            {
                if (_palette == null)
                {
                    _view = new ChatView(AppSettings.Load());
                    _view.AttentionRequested += () =>
                    {
                        if (_palette is { Visible: false })
                        {
                            _palette.Visible = true;
                            WriteLine("\nClaude is waiting for your approval in the Claude panel.\n");
                        }
                    };
                    _palette = new PaletteSet("Claude", "CLAUDE", PaletteId)
                    {
                        Style = PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton |
                                PaletteSetStyles.ShowPropertiesMenu | PaletteSetStyles.Snappable,
                        MinimumSize = new Size(320, 420),
                        DockEnabled = DockSides.Left | DockSides.Right,
                        // Keep keystrokes in the chat box instead of AutoCAD's command line.
                        KeepFocus = true,
                    };
                    _palette.AddVisual("Chat", _view);
                    _palette.Visible = true;
                    if (_palette.Dock == DockSides.None)
                    {
                        _palette.Size = new Size(420, 720);
                        _palette.Dock = DockSides.Right;
                    }
                }
                else
                {
                    _palette.Visible = true;
                }
                _palette.Activate(0);
                _view?.FocusInput();
            }
            catch (System.Exception ex)
            {
                Log.Write("show: " + ex);
                WriteLine("\nClaude panel could not open: " + ex.Message + "\n");
            }
        }

        public static void Toggle()
        {
            if (_palette is { Visible: true }) _palette.Visible = false;
            else Show();
        }

        internal static void WriteLine(string text)
        {
            try { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(text); }
            catch (System.Exception) { }
        }
    }

    public sealed class Commands
    {
        /// <summary>CLAUDE: open (or hide) the chat panel.</summary>
        [CommandMethod("CLAUDE", CommandFlags.Session | CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Claude() => Plugin.Toggle();

        /// <summary>CLAUDEOPEN: always open it (for toolbar buttons and scripts).</summary>
        [CommandMethod("CLAUDEOPEN", CommandFlags.Session | CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ClaudeOpen() => Plugin.Show();
    }
}
