using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoCADChat
{
    /// <summary>The chat panel: messages, tool activity, approvals and the input box.</summary>
    public sealed class ChatView : UserControl
    {
        private enum State { Offline, Starting, Ready, Busy }

        private Theme _t;
        private AppSettings _settings;
        private readonly BridgeClient _bridge;
        private State _state = State.Offline;

        private readonly ScrollViewer _scroll = new();
        private readonly StackPanel _messages = new();
        private readonly TextBox _input = new();
        private readonly Button _send;
        private readonly TextBlock _statusText = new();
        private readonly Border _statusDot = new();
        private readonly TextBlock _footer = new();
        private readonly Border _root = new();
        private Border? _welcome;

        // The answer being streamed: raw text, re-rendered as Markdown a few times a second.
        private RichTextBox? _answerBox;
        private readonly StringBuilder _answer = new();
        private bool _answerDirty;
        private readonly DispatcherTimer _renderTimer;
        private StackPanel? _activity; // tool rows of the current turn
        private readonly Dictionary<string, (TextBlock icon, TextBlock summary, StackPanel row)> _tools = new();
        private long _contextTokens;
        private string _model;
        private bool _fatalShown; // a fatal error card already explains why the bridge is about to exit

        private const double FontSz = 13;

        /// <summary>Raised when Claude is waiting on the user (an approval), so a hidden panel can be shown.</summary>
        public event Action? AttentionRequested;

        public ChatView(AppSettings settings)
        {
            _settings = settings;
            _model = settings.Model;
            _t = CadContext.IsDarkTheme() ? Theme.Dark() : Theme.Light();
            _bridge = new BridgeClient(Dispatcher);
            _bridge.EventReceived += OnBridgeEvent;
            _bridge.SetupProgress += line => SetStatus(State.Starting, "First-time setup: " + Trim(line, 60));
            _bridge.Exited += OnBridgeExited;
            _send = Ui.Button("Send", _t, primary: true, tooltip: "Send (Enter). Shift+Enter for a new line.");
            _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => FlushAnswer(), Dispatcher);
            Build();
            Loaded += (_, _) => { if (_state == State.Offline) StartBridge(); };
        }

        // ---------------------------------------------------------------- layout

        private void Build()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // header
            var header = new DockPanel { Margin = new Thickness(10, 8, 8, 6), LastChildFill = true };
            var newChat = Ui.Button("New chat", _t, tooltip: "Start a new conversation");
            newChat.Click += (_, _) => NewChat();
            var gear = Ui.Button("Settings", _t, tooltip: "API key, model, folders, safety");
            gear.Click += (_, _) => OpenSettings();
            gear.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(gear, Dock.Right);
            DockPanel.SetDock(newChat, Dock.Right);
            header.Children.Add(gear);
            header.Children.Add(newChat);
            _statusDot.Width = _statusDot.Height = 8;
            _statusDot.CornerRadius = new CornerRadius(4);
            _statusDot.VerticalAlignment = VerticalAlignment.Center;
            _statusDot.Margin = new Thickness(0, 0, 8, 0);
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(Ui.Text("Claude", _t.Text, 15, FontWeights.SemiBold));
            title.Children.Add(new Border { Width = 10 });
            title.Children.Add(_statusDot);
            _statusText.Foreground = _t.Muted;
            _statusText.FontSize = 11.5;
            _statusText.VerticalAlignment = VerticalAlignment.Center;
            _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Children.Add(_statusText);
            header.Children.Add(title);
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            // messages
            _messages.Margin = new Thickness(10, 4, 10, 10);
            _scroll.Content = _messages;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.Background = _t.Background;
            _scroll.BorderBrush = _t.Border;
            Grid.SetRow(_scroll, 1);
            grid.Children.Add(_scroll);

            // input
            var inputArea = new Grid { Margin = new Thickness(10, 6, 10, 8) };
            inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var box = new Border
            {
                Background = _t.Surface,
                BorderBrush = _t.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 6, 6),
            };
            var row = new DockPanel();
            _send.VerticalAlignment = VerticalAlignment.Bottom;
            _send.Margin = new Thickness(6, 0, 0, 0);
            _send.Click += (_, _) => { if (_state == State.Busy) Cancel(); else SendInput(); };
            DockPanel.SetDock(_send, Dock.Right);
            row.Children.Add(_send);
            _input.AcceptsReturn = true;
            _input.TextWrapping = TextWrapping.Wrap;
            _input.MinHeight = 38;
            _input.MaxHeight = 160;
            _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _input.BorderThickness = new Thickness(0);
            _input.Background = Brushes.Transparent;
            _input.Foreground = _t.Text;
            _input.CaretBrush = _t.Text;
            _input.FontSize = FontSz;
            _input.SpellCheck.IsEnabled = true;
            _input.PreviewKeyDown += OnInputKey;
            row.Children.Add(_input);
            box.Child = row;
            Grid.SetRow(box, 0);
            inputArea.Children.Add(box);
            _footer.Foreground = _t.Muted;
            _footer.FontSize = 11;
            _footer.Margin = new Thickness(2, 4, 0, 0);
            _footer.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(_footer, 1);
            inputArea.Children.Add(_footer);
            Grid.SetRow(inputArea, 2);
            grid.Children.Add(inputArea);

            _root.Background = _t.Background;
            _root.Child = grid;
            Content = _root;
            ShowWelcome();
            UpdateFooter();
            SetStatus(State.Offline, "Not started");
        }

        private void ShowWelcome()
        {
            var panel = new StackPanel { Margin = new Thickness(2, 10, 2, 10) };
            panel.Children.Add(Ui.Text("Ask Claude to work on this drawing.", _t.Text, 14, FontWeights.SemiBold));
            panel.Children.Add(new Border { Height = 6 });
            panel.Children.Add(Ui.Text("It can read, draw and change the open drawing — and nothing else on this computer. Select objects first to talk about \"these\". Ctrl+Z undoes anything it does.", _t.Muted, 12));
            panel.Children.Add(new Border { Height = 10 });
            string[] examples =
            {
                "What's in this drawing? Summarise the layers and objects.",
                "Draw a 200 × 100 mm rectangle with 10 mm fillets at the origin.",
                "Put the selected objects on a new layer called DETAIL, colour 3.",
                "Dimension everything on layer GEOMETRY.",
                "Check this drawing for problems and tell me what you'd fix.",
            };
            foreach (var ex in examples)
            {
                var chip = Ui.Button(ex, _t, fontSize: 12);
                chip.HorizontalAlignment = HorizontalAlignment.Left;
                chip.HorizontalContentAlignment = HorizontalAlignment.Left;
                chip.Margin = new Thickness(0, 0, 0, 6);
                chip.Content = new TextBlock { Text = ex, TextWrapping = TextWrapping.Wrap, Foreground = _t.Text };
                chip.Click += (_, _) => { _input.Text = ex; _input.CaretIndex = ex.Length; _input.Focus(); };
                panel.Children.Add(chip);
            }
            _welcome = new Border { Child = panel };
            _messages.Children.Add(_welcome);
        }

        // ------------------------------------------------------------ bridge life

        private void StartBridge()
        {
            if (_settings.UsesApiKey && !_settings.HasApiKey)
            {
                SetStatus(State.Offline, "Needs an API key");
                AddNotice("Add your Anthropic API key to start. It is stored encrypted for your Windows account only.", "Open Settings", OpenSettings);
                return;
            }
            _fatalShown = false;
            SetStatus(State.Starting, "Starting…");
            var problem = _bridge.Start(_settings);
            if (problem != null)
            {
                SetStatus(State.Offline, "Not running");
                AddNotice(problem, "Settings", OpenSettings);
            }
        }

        private void OnBridgeExited(int code, string stderrTail)
        {
            var wasBusy = _state == State.Busy;
            EndTurnUi();
            SetStatus(State.Offline, "Stopped");
            Log.Write($"bridge exited with {code}: {stderrTail}");
            if (_fatalShown) return;
            var hint = string.IsNullOrWhiteSpace(stderrTail) ? "" : "\n\n" + Trim(stderrTail, 600);
            AddNotice((wasBusy ? "The connection stopped in the middle of a reply." : "The AutoCAD tools stopped.") +
                      $" (exit code {code}){hint}\n\nLogs: {Paths.LogDir}", "Restart", () => { StartBridge(); });
        }

        private void OnBridgeEvent(JsonElement ev)
        {
            var type = Str(ev, "type");
            switch (type)
            {
                case "status":
                    if (_state is State.Starting or State.Offline) SetStatus(State.Starting, Str(ev, "text"));
                    else if (_welcome == null) AddStatusLine(Str(ev, "text"));
                    break;
                case "ready":
                    _model = Str(ev, "model");
                    SetStatus(State.Ready, "Ready");
                    UpdateFooter();
                    break;
                case "needs_login":
                    _fatalShown = true;
                    SetStatus(State.Offline, "Not signed in");
                    AddSignIn();
                    break;
                case "turn_start":
                    SetStatus(State.Busy, "Working…");
                    break;
                case "text_delta":
                    AppendAnswer(Str(ev, "text"));
                    break;
                case "progress":
                    FinishAnswerBlock();
                    AddProgress(Str(ev, "text"));
                    break;
                case "tool_start":
                    FinishAnswerBlock();
                    AddToolRow(Str(ev, "id"), Str(ev, "label"));
                    break;
                case "tool_end":
                    FinishToolRow(Str(ev, "id"), ev.TryGetProperty("ok", out var ok) && ok.GetBoolean(), Str(ev, "summary"),
                        ev.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String ? img.GetString() : null);
                    break;
                case "confirm_request":
                    FinishAnswerBlock();
                    AddConfirm(Str(ev, "id"), Str(ev, "detail"));
                    break;
                case "turn_end":
                    if (ev.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object && u.TryGetProperty("context", out var c))
                        _contextTokens = c.GetInt64();
                    var reason = Str(ev, "stop_reason");
                    EndTurnUi();
                    if (reason == "cancelled") AddStatusLine("Stopped.");
                    SetStatus(_bridge.IsRunning ? State.Ready : State.Offline, _bridge.IsRunning ? "Ready" : "Stopped");
                    UpdateFooter();
                    break;
                case "error":
                    FinishAnswerBlock();
                    var fatal = ev.TryGetProperty("fatal", out var f) && f.GetBoolean();
                    if (fatal)
                    {
                        _fatalShown = true;
                        AddNotice(Str(ev, "message"), "Settings", OpenSettings);
                    }
                    else AddError(Str(ev, "message"));
                    break;
            }
        }

        // ---------------------------------------------------------------- actions

        private void OnInputKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (_state != State.Busy) SendInput();
            }
            else if (e.Key == Key.Escape && _state == State.Busy)
            {
                e.Handled = true;
                Cancel();
            }
        }

        private void SendInput()
        {
            var text = _input.Text.Trim();
            if (text.Length == 0) return;
            if (_state == State.Offline)
            {
                StartBridge();
                if (_state == State.Offline) return;
            }
            if (_state == State.Starting)
            {
                AddStatusLine("Still starting — your message will be sent when the tools are ready.");
                QueueUntilReady(text);
                _input.Clear();
                return;
            }
            _input.Clear();
            Dispatch(text);
        }

        private string? _queued;

        private void QueueUntilReady(string text)
        {
            _queued = text;
            AddUserMessage(text);
        }

        private void Dispatch(string text, bool alreadyShown = false)
        {
            if (!alreadyShown) AddUserMessage(text);
            var ctx = CadContext.Capture();
            _bridge.Send(new Dictionary<string, object?> { ["type"] = "send", ["text"] = text, ["context"] = ctx });
            SetStatus(State.Busy, "Working…");
        }

        private void Cancel()
        {
            _bridge.Send(new { type = "cancel" });
            SetStatus(State.Busy, "Stopping…");
        }

        private void NewChat()
        {
            if (_bridge.IsRunning) _bridge.Send(new { type = "reset" });
            EndTurnUi();
            _messages.Children.Clear();
            _contextTokens = 0;
            ShowWelcome();
            UpdateFooter();
            if (_state == State.Busy) SetStatus(State.Ready, "Ready");
            _input.Focus();
        }

        private void OpenSettings()
        {
            var before = _settings.Clone();
            var win = new SettingsWindow(_settings, _t);
            var owner = Window.GetWindow(this);
            if (owner != null) win.Owner = owner;
            if (win.ShowDialog() != true) return;
            _settings = win.Result;
            _settings.Save();
            if (_settings.NeedsRestartComparedTo(before) || !_bridge.IsRunning)
            {
                _bridge.Stop();
                SetStatus(State.Offline, "Restarting…");
                StartBridge();
            }
            else
            {
                _bridge.Send(new { type = "settings", model = _settings.Model, effort = _settings.Effort, confirm_destructive = _settings.ConfirmDestructive });
                _model = _settings.Model;
            }
            UpdateFooter();
        }

        // ------------------------------------------------------------ message UI

        private void AddUserMessage(string text)
        {
            RemoveWelcome();
            var bubble = new Border
            {
                Background = _t.UserBubble,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(30, 8, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = Ui.SelectableText(text, _t.Text, FontSz),
            };
            Add(bubble, forceScroll: true);
        }

        private void AppendAnswer(string delta)
        {
            if (_answerBox == null)
            {
                RemoveWelcome();
                _answerBox = new RichTextBox
                {
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = _t.Text,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 4, 0, 4),
                    IsDocumentEnabled = true,
                };
                _answer.Clear();
                Add(_answerBox);
                _renderTimer.Start();
            }
            _answer.Append(delta);
            _answerDirty = true;
        }

        private void FlushAnswer()
        {
            if (_answerBox == null || !_answerDirty) return;
            bool atBottom = IsAtBottom();
            _answerBox.Document = Markdown.Render(_answer.ToString().TrimEnd(), _t, FontSz);
            _answerDirty = false;
            if (atBottom) _scroll.ScrollToEnd();
        }

        private void FinishAnswerBlock()
        {
            FlushAnswer();
            _renderTimer.Stop();
            _answerBox = null;
            _answer.Clear();
            _activity = null;
        }

        private StackPanel Activity()
        {
            if (_activity == null)
            {
                _activity = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                Add(_activity);
            }
            return _activity;
        }

        private void AddProgress(string text)
        {
            var tb = Ui.Text(text, _t.Muted, 12);
            tb.FontStyle = FontStyles.Italic;
            tb.Margin = new Thickness(0, 4, 0, 4);
            Add(tb);
        }

        private void AddToolRow(string id, string label)
        {
            var row = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
            var line = new DockPanel();
            var icon = Ui.Text("◌", _t.Muted, 12);
            icon.Width = 16;
            DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(icon);
            var name = Ui.Text(label, _t.Text, 12, FontWeights.SemiBold);
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var summary = Ui.Text("", _t.Muted, 11.5);
            var stack = new StackPanel();
            stack.Children.Add(name);
            stack.Children.Add(summary);
            line.Children.Add(stack);
            row.Children.Add(line);
            var host = new Border
            {
                Background = _t.Surface,
                BorderBrush = _t.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 2, 0, 2),
                Child = row,
            };
            Activity().Children.Add(host);
            _tools[id] = (icon, summary, row);
            if (IsAtBottom(80)) _scroll.ScrollToEnd();
        }

        private void FinishToolRow(string id, bool ok, string summary, string? imageB64)
        {
            if (!_tools.TryGetValue(id, out var r)) return;
            r.icon.Text = ok ? "✓" : "✕";
            r.icon.Foreground = ok ? _t.Ok : _t.Bad;
            r.summary.Text = Trim(summary, 220);
            r.summary.Visibility = string.IsNullOrWhiteSpace(summary) ? Visibility.Collapsed : Visibility.Visible;
            if (imageB64 != null)
            {
                var bmp = Ui.DecodePng(imageB64);
                if (bmp != null)
                {
                    var img = new Image { Source = bmp, MaxHeight = 180, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 4, 0, 2), Cursor = Cursors.Hand, ToolTip = "Click to enlarge" };
                    img.MouseLeftButtonUp += (_, _) => Ui.ShowImageWindow(bmp, _t);
                    r.row.Children.Add(img);
                }
            }
            if (IsAtBottom(200)) _scroll.ScrollToEnd();
        }

        private void AddConfirm(string id, string detail)
        {
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text("Approve this change?", _t.Warn, 12.5, FontWeights.SemiBold));
            panel.Children.Add(Ui.SelectableText(detail, _t.Text, 12));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var allow = Ui.Button("Allow", _t, primary: true);
            var deny = Ui.Button("Don't allow", _t);
            deny.Margin = new Thickness(8, 0, 0, 0);
            var outcome = Ui.Text("", _t.Muted, 12);
            outcome.Margin = new Thickness(10, 3, 0, 0);
            void Answer(bool yes)
            {
                allow.IsEnabled = deny.IsEnabled = false;
                outcome.Text = yes ? "Allowed" : "Not allowed";
                _bridge.Send(new { type = "confirm", id, approved = yes });
            }
            allow.Click += (_, _) => Answer(true);
            deny.Click += (_, _) => Answer(false);
            buttons.Children.Add(allow);
            buttons.Children.Add(deny);
            buttons.Children.Add(outcome);
            panel.Children.Add(buttons);
            Add(new Border
            {
                Background = _t.Surface,
                BorderBrush = _t.Warn,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 6, 0, 6),
                Child = panel,
            }, forceScroll: true);
            AttentionRequested?.Invoke();
            allow.Focus();
        }

        private void AddError(string message)
        {
            var tb = Ui.SelectableText(message, _t.Bad, 12.5);
            Add(new Border { Margin = new Thickness(0, 6, 0, 6), Child = tb }, forceScroll: true);
        }

        private void AddStatusLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var tb = Ui.Text(text, _t.Muted, 11.5);
            tb.Margin = new Thickness(0, 4, 0, 4);
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            Add(tb);
        }

        private void AddNotice(string message, string action, Action onClick)
        {
            RemoveWelcome();
            var panel = new StackPanel();
            panel.Children.Add(Ui.SelectableText(message, _t.Text, 12.5));
            var b = Ui.Button(action, _t, primary: true);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 8, 0, 0);
            b.Click += (_, _) => onClick();
            panel.Children.Add(b);
            Add(new Border
            {
                Background = _t.Surface,
                BorderBrush = _t.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 8, 0, 8),
                Child = panel,
            }, forceScroll: true);
        }

        private void AddSignIn()
        {
            RemoveWelcome();
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text("Sign in with your Claude account", _t.Text, 13, FontWeights.SemiBold));
            panel.Children.Add(Ui.SelectableText("Claude Code signs in once in your browser. Click Sign in, finish in the browser window that opens, then click Restart.", _t.Text, 12.5));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var signIn = Ui.Button("Sign in", _t, primary: true);
            signIn.Click += (_, _) => OpenSignIn();
            var restart = Ui.Button("Restart", _t);
            restart.Margin = new Thickness(8, 0, 0, 0);
            restart.Click += (_, _) => StartBridge();
            row.Children.Add(signIn);
            row.Children.Add(restart);
            panel.Children.Add(row);
            Add(new Border
            {
                Background = _t.Surface,
                BorderBrush = _t.Accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 8, 0, 8),
                Child = panel,
            }, forceScroll: true);
        }

        private static void OpenSignIn()
        {
            var claude = Paths.FindClaude();
            var exe = claude == null ? "claude" : "& '" + claude.Replace("'", "''") + "'";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoExit -NoProfile -Command \"" + exe + " auth login\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Write("sign-in: " + ex.Message);
            }
        }

        private void RemoveWelcome()
        {
            if (_welcome != null)
            {
                _messages.Children.Remove(_welcome);
                _welcome = null;
            }
        }

        private void Add(UIElement e, bool forceScroll = false)
        {
            bool atBottom = IsAtBottom();
            _messages.Children.Add(e);
            if (atBottom || forceScroll) _scroll.ScrollToEnd();
        }

        private bool IsAtBottom(double slack = 40) =>
            _scroll.VerticalOffset >= _scroll.ScrollableHeight - slack;

        private void EndTurnUi()
        {
            FinishAnswerBlock();
            _tools.Clear();
        }

        private void SetStatus(State s, string text)
        {
            var was = _state;
            _state = s;
            _statusText.Text = text;
            _statusDot.Background = s switch
            {
                State.Ready => _t.Ok,
                State.Busy => _t.Accent,
                State.Starting => _t.Warn,
                _ => _t.Bad,
            };
            _send.Content = s == State.Busy ? "Stop" : "Send";
            _send.ToolTip = s == State.Busy ? "Stop Claude (Esc)" : "Send (Enter). Shift+Enter for a new line.";
            if (s == State.Ready && was != State.Ready && _queued != null)
            {
                var q = _queued;
                _queued = null;
                Dispatch(q, alreadyShown: true);
            }
        }

        private void UpdateFooter()
        {
            var parts = new List<string> { _settings.UsesApiKey ? "API key" : "Claude account", _model.Replace("claude-", ""), _settings.Effort + " effort" };
            if (_settings.ReadOnly) parts.Add("read-only");
            if (!_settings.ConfirmDestructive) parts.Add("no approvals");
            if (_contextTokens > 0) parts.Add("context " + Ui.FormatTokens(_contextTokens));
            _footer.Text = string.Join("  ·  ", parts);
            _footer.Foreground = _contextTokens > 600_000 ? _t.Warn : _t.Muted;
            _footer.ToolTip = _contextTokens > 600_000 ? "This conversation is long. Start a new chat to keep answers fast." : null;
        }

        public void Shutdown()
        {
            _renderTimer.Stop();
            _bridge.Dispose();
        }

        public void FocusInput() => _input.Focus();

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
