using System.Windows.Media;

namespace AutoCADChat
{
    /// <summary>Colours that follow AutoCAD's own theme (COLORTHEME 0 = dark, 1 = light).</summary>
    public sealed class Theme
    {
        public Brush Background = null!, Surface = null!, SurfaceAlt = null!, Border = null!;
        public Brush Text = null!, Muted = null!, Accent = null!, AccentText = null!;
        public Brush UserBubble = null!, Ok = null!, Bad = null!, Warn = null!, CodeBg = null!;
        public bool IsDark;

        public static Theme Dark() => new()
        {
            IsDark = true,
            Background = B("#21252B"), Surface = B("#2B3038"), SurfaceAlt = B("#323843"), Border = B("#3E4552"),
            Text = B("#E6E8EB"), Muted = B("#98A1AE"), Accent = B("#D97757"), AccentText = B("#FFFFFF"),
            UserBubble = B("#3A4250"), Ok = B("#5FB878"), Bad = B("#E06C6C"), Warn = B("#E0B05C"), CodeBg = B("#1B1F24"),
        };

        public static Theme Light() => new()
        {
            IsDark = false,
            Background = B("#F4F5F7"), Surface = B("#FFFFFF"), SurfaceAlt = B("#ECEEF1"), Border = B("#D5D9DF"),
            Text = B("#1F2328"), Muted = B("#5E6673"), Accent = B("#C2603F"), AccentText = B("#FFFFFF"),
            UserBubble = B("#E3E8EF"), Ok = B("#2E8B4E"), Bad = B("#C23B3B"), Warn = B("#A86E00"), CodeBg = B("#F0F1F3"),
        };

        private static Brush B(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}
