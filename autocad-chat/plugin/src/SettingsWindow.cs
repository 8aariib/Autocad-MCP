using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoCADChat
{
    /// <summary>API key, model, effort, folders and safety switches.</summary>
    public sealed class SettingsWindow : Window
    {
        public AppSettings Result { get; private set; }

        private readonly PasswordBox _key = new();
        private readonly ComboBox _engine = new();
        private readonly StackPanel _keyPanel = new();
        private readonly ComboBox _model = new();
        private readonly ComboBox _effort = new();
        private readonly TextBox _folders = new();
        private readonly CheckBox _confirm = new();
        private readonly CheckBox _readOnly = new();
        private readonly CheckBox _openOnStart = new();
        private readonly TextBox _uv = new();
        private readonly Theme _t;
        private readonly bool _hadKey;

        public SettingsWindow(AppSettings current, Theme t)
        {
            _t = t;
            Result = current.Clone();
            _hadKey = current.HasApiKey;
            Title = "Claude for AutoCAD — Settings";
            Width = 500;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = t.Background;
            ShowInTaskbar = false;

            var p = new StackPanel { Margin = new Thickness(16) };

            p.Children.Add(Label("Sign in with"));
            _engine.Items.Add("My Claude account (no API key; uses Claude Code)");
            _engine.Items.Add("An Anthropic API key");
            _engine.SelectedIndex = current.UsesApiKey ? 1 : 0;
            p.Children.Add(_engine);
            p.Children.Add(Hint("Your Claude account uses your Pro or Max plan's included usage. An API key is billed separately by Anthropic."));

            _keyPanel.Children.Add(Label("Anthropic API key"));
            ApplyStyle(_key);
            _keyPanel.Children.Add(_key);
            p.Children.Add(_keyPanel);
            _engine.SelectionChanged += (_, _) => _keyPanel.Visibility = _engine.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            _keyPanel.Visibility = current.UsesApiKey ? Visibility.Visible : Visibility.Collapsed;
            var keyHelp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            keyHelp.Children.Add(Ui.Text(_hadKey ? "A key is saved. Leave empty to keep it." : "Create one at console.anthropic.com → API keys.", t.Muted, 11.5));
            var link = Ui.Button("Open console", t, fontSize: 11);
            link.Margin = new Thickness(8, 0, 0, 0);
            link.Padding = new Thickness(6, 1, 6, 1);
            link.Click += (_, _) => OpenUrl("https://console.anthropic.com/settings/keys");
            keyHelp.Children.Add(link);
            if (_hadKey)
            {
                var clear = Ui.Button("Remove key", t, fontSize: 11);
                clear.Margin = new Thickness(6, 0, 0, 0);
                clear.Padding = new Thickness(6, 1, 6, 1);
                clear.Click += (_, _) => { Result.ApiKey = ""; clear.IsEnabled = false; clear.Content = "Removed"; };
                keyHelp.Children.Add(clear);
            }
            _keyPanel.Children.Add(keyHelp);

            p.Children.Add(Label("Model"));
            foreach (var m in AppSettings.Models) _model.Items.Add(m);
            _model.SelectedItem = current.Model;
            p.Children.Add(_model);
            p.Children.Add(Hint("Opus is the most capable; Sonnet is faster and cheaper; Haiku is the fastest and cheapest."));

            p.Children.Add(Label("Effort"));
            foreach (var e in AppSettings.Efforts) _effort.Items.Add(e);
            _effort.SelectedItem = current.Effort;
            p.Children.Add(_effort);
            p.Children.Add(Hint("Higher effort thinks longer: better on hard drawings, slower and costlier."));

            p.Children.Add(Label("Folders Claude may open and save drawings in (one per line)"));
            ApplyStyle(_folders);
            _folders.AcceptsReturn = true;
            _folders.TextWrapping = TextWrapping.NoWrap;
            _folders.MinHeight = 54;
            _folders.Text = string.Join(Environment.NewLine, current.AllowedFolders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            p.Children.Add(_folders);

            p.Children.Add(new Border { Height = 10 });
            _confirm.Content = Ui.Text("Ask me before Claude deletes, purges or makes other destructive changes", t.Text, 12.5);
            _confirm.IsChecked = current.ConfirmDestructive;
            p.Children.Add(_confirm);
            _readOnly.Content = Ui.Text("Read-only: Claude can look at the drawing but not change it", t.Text, 12.5);
            _readOnly.IsChecked = current.ReadOnly;
            _readOnly.Margin = new Thickness(0, 6, 0, 0);
            p.Children.Add(_readOnly);
            _openOnStart.Content = Ui.Text("Open this panel when AutoCAD starts", t.Text, 12.5);
            _openOnStart.IsChecked = current.OpenOnStartup;
            _openOnStart.Margin = new Thickness(0, 6, 0, 0);
            p.Children.Add(_openOnStart);

            var adv = new Expander { Header = Ui.Text("Advanced", t.Muted, 12), Margin = new Thickness(0, 10, 0, 0), Foreground = t.Text };
            var advPanel = new StackPanel();
            advPanel.Children.Add(Label("uv.exe path (empty = find it automatically)"));
            ApplyStyle(_uv);
            _uv.Text = current.UvPath;
            advPanel.Children.Add(_uv);
            var logs = Ui.Button("Open log folder", t, fontSize: 11.5);
            logs.HorizontalAlignment = HorizontalAlignment.Left;
            logs.Margin = new Thickness(0, 8, 0, 0);
            logs.Click += (_, _) => OpenUrl(Paths.LogDir);
            advPanel.Children.Add(logs);
            adv.Content = advPanel;
            p.Children.Add(adv);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var save = Ui.Button("Save", t, primary: true);
            save.IsDefault = true;
            save.Click += (_, _) => Commit();
            var cancel = Ui.Button("Cancel", t);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            p.Children.Add(buttons);

            Content = p;
            Loaded += (_, _) => { if (current.UsesApiKey && !_hadKey) _key.Focus(); };
        }

        private void Commit()
        {
            Result.Engine = _engine.SelectedIndex == 1 ? AppSettings.EngineApi : AppSettings.EngineClaudeCode;
            var key = _key.Password.Trim();
            if (Result.UsesApiKey && key.Length > 0)
            {
                if (!key.StartsWith("sk-ant-"))
                {
                    MessageBox.Show(this, "That does not look like an Anthropic API key (they start with \"sk-ant-\").",
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Result.ApiKey = key;
            }
            Result.Model = _model.SelectedItem as string ?? Result.Model;
            Result.Effort = _effort.SelectedItem as string ?? Result.Effort;
            Result.AllowedFolders = string.Join(";", _folders.Text.Split(new[] { '\r', '\n', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Result.ConfirmDestructive = _confirm.IsChecked == true;
            Result.ReadOnly = _readOnly.IsChecked == true;
            Result.OpenOnStartup = _openOnStart.IsChecked == true;
            Result.UvPath = _uv.Text.Trim();
            DialogResult = true;
        }

        private TextBlock Label(string text)
        {
            var l = Ui.Text(text, _t.Text, 12.5, FontWeights.SemiBold);
            l.Margin = new Thickness(0, 10, 0, 4);
            return l;
        }

        private TextBlock Hint(string text)
        {
            var h = Ui.Text(text, _t.Muted, 11.5);
            h.Margin = new Thickness(0, 3, 0, 0);
            return h;
        }

        private void ApplyStyle(Control c)
        {
            c.Background = _t.Surface;
            c.Foreground = _t.Text;
            c.BorderBrush = _t.Border;
            c.Padding = new Thickness(6, 4, 6, 4);
            c.FontSize = 12.5;
            if (c is TextBox tb) tb.CaretBrush = _t.Text;
            if (c is PasswordBox pb) pb.CaretBrush = _t.Text;
        }

        private static void OpenUrl(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("open: " + ex.Message); }
        }
    }
}
