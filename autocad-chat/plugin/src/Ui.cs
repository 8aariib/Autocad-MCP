using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoCADChat
{
    /// <summary>Small builders for the code-only WPF UI (no XAML, so it builds anywhere).</summary>
    internal static class Ui
    {
        public static Button Button(string text, Theme t, bool primary = false, string? tooltip = null, double fontSize = 12.5)
        {
            var b = new Button
            {
                Content = text,
                Foreground = primary ? t.AccentText : t.Text,
                Background = primary ? t.Accent : t.SurfaceAlt,
                BorderBrush = primary ? t.Accent : t.Border,
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = fontSize,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Template = FlatTemplate(),
                Focusable = true,
            };
            return b;
        }

        private static ControlTemplate? _flat;

        private static ControlTemplate FlatTemplate()
        {
            if (_flat != null) return _flat;
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
            var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "bd"));
            tpl.Triggers.Add(hover);
            tpl.Triggers.Add(pressed);
            tpl.Triggers.Add(disabled);
            tpl.Triggers.Add(focus);
            _flat = tpl;
            return tpl;
        }

        public static TextBlock Text(string text, Brush fg, double size = 12.5, FontWeight? weight = null)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = fg,
                FontSize = size,
                FontWeight = weight ?? FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        /// <summary>Selectable, copyable text that looks like a label.</summary>
        public static TextBox SelectableText(string text, Brush fg, double size = 13)
        {
            return new TextBox
            {
                Text = text,
                Foreground = fg,
                FontSize = size,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                IsTabStop = false,
            };
        }

        public static BitmapImage? DecodePng(string base64)
        {
            try
            {
                var bytes = Convert.FromBase64String(base64);
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex)
            {
                Log.Write("image decode: " + ex.Message);
                return null;
            }
        }

        public static void ShowImageWindow(BitmapImage img, Theme t)
        {
            var w = new Window
            {
                Title = "Claude — view",
                Background = t.Background,
                Width = Math.Min(img.PixelWidth + 40, SystemParameters.WorkArea.Width * 0.9),
                Height = Math.Min(img.PixelHeight + 60, SystemParameters.WorkArea.Height * 0.9),
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new Image { Source = img, Stretch = Stretch.None },
                },
            };
            w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.Show();
        }

        public static string FormatTokens(long n) =>
            n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();
    }
}
