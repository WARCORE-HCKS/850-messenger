using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Area850.Services;

internal static class EmojiBank
{
    public static IReadOnlyList<Smile> Pack { get; } = SmileArt.Pack;

    public static void Fill(Panel panel, TextBox composer, Popup? pop)
    {
        panel.Children.Clear();
        foreach (var s in Pack)
        {
            var smile = s;
            var img = new Image { Source = smile.Image, Width = 28, Height = 28, Stretch = Stretch.Uniform };
            var b = new Button
            {
                Content = img,
                Width = 36,
                Height = 36,
                Padding = new Thickness(2),
                ToolTip = smile.Name + "   " + smile.Shortcut
            };
            b.Click += (_, _) =>
            {
                var t = composer.Text ?? "";
                if (t.Length > 0 && !char.IsWhiteSpace(t[^1])) t += " ";
                composer.Text = t + smile.Token + " ";
                if (pop is not null) pop.IsOpen = false;
                composer.CaretIndex = composer.Text.Length;
                composer.Focus();
            };
            panel.Children.Add(b);
        }
    }

    public static string Expand(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (sc, tok) in Pack.SelectMany(s => s.Shortcuts.Select(k => (k, s.Token))).OrderByDescending(p => p.k.Length))
            text = text.Replace(sc, " " + tok + " ", StringComparison.Ordinal);
        return Regex.Replace(text, @" {2,}", " ").Trim();
    }

    public static TextBlock Message(string text, Brush ink, double size = 13.5)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = ink };
        Paint(tb, text);
        tb.ContextMenu = CopyMenu(text);
        return tb;
    }

    public static void Paint(TextBlock tb, string text)
    {
        tb.Inlines.Clear();
        if (string.IsNullOrEmpty(text)) return;
        var i = 0;
        while (i < text.Length)
        {
            var hit = Next(text, i);
            if (hit is null)
            {
                tb.Inlines.Add(new Run(text[i..]));
                break;
            }
            if (hit.Value.at > i)
                tb.Inlines.Add(new Run(text[i..hit.Value.at]));
            tb.Inlines.Add(new InlineUIContainer(new Image
            {
                Source = hit.Value.smile.Image,
                Width = 22,
                Height = 22,
                Margin = new Thickness(1, -3, 1, -3),
                Stretch = Stretch.Uniform
            }));
            i = hit.Value.at + hit.Value.len;
        }
    }

    public static ContextMenu CopyMenu(string text)
    {
        var m = new ContextMenu();
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => Clipboard.SetText(text ?? "");
        m.Items.Add(copy);
        return m;
    }

    private static (int at, int len, Smile smile)? Next(string text, int start)
    {
        var bestAt = int.MaxValue;
        (int at, int len, Smile smile)? best = null;
        foreach (var s in Pack)
        {
            var p = text.IndexOf(s.Token, start, StringComparison.OrdinalIgnoreCase);
            if (p >= 0 && p < bestAt)
            {
                bestAt = p;
                best = (p, s.Token.Length, s);
            }
        }
        return best;
    }
}

internal sealed class Smile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Token { get; init; }
    public required string Shortcut { get; init; }
    public required string[] Shortcuts { get; init; }
    public required ImageSource Image { get; init; }
}

internal static class SmileArt
{
    private static readonly Color Face = Color.FromRgb(255, 210, 74);
    private static readonly Color FaceDeep = Color.FromRgb(240, 168, 32);
    private static readonly Color Line = Color.FromRgb(176, 96, 18);
    private static readonly Color Eye = Color.FromRgb(48, 36, 18);
    private static readonly Color Mouth = Color.FromRgb(196, 43, 28);
    private static readonly Color Green = Color.FromRgb(39, 208, 108);
    private static readonly Color White = Colors.White;

    public static IReadOnlyList<Smile> Pack { get; } = Build();

    private static List<Smile> Build() =>
    [
        Make("smile", "Smile", ":)", [":)", ":-)"], dc => FaceBase(dc, (c) => {
            Eyes(c, 0, 0); ArcMouth(c, false);
        })),
        Make("grin", "Grin", ":D", [":D", ":-D"], dc => FaceBase(dc, (c) => {
            Eyes(c, 0, 0); OpenMouth(c, true);
        })),
        Make("wink", "Wink", ";)", [";)", ";-)"], dc => FaceBase(dc, (c) => {
            EyeDot(c, 22, 26); Wink(c, 40, 26); ArcMouth(c, false);
        })),
        Make("tongue", "Tongue", ":P", [":P", ":-P", ":p", ":-p"], dc => FaceBase(dc, (c) => {
            Eyes(c, 0, 0); Tongue(c);
        })),
        Make("laugh", "Laugh", "xD", ["xD", "XD"], dc => FaceBase(dc, (c) => {
            HappyEyes(c); OpenMouth(c, true);
        })),
        Make("kiss", "Kiss", ":*", [":*", ":-*"], dc => FaceBase(dc, (c) => {
            Eyes(c, 0, 2); Kiss(c);
        })),
        Make("cool", "Cool", "8)", ["8)", "8-)"], dc => FaceBase(dc, (c) => {
            Shades(c); ArcMouth(c, false);
        })),
        Make("blush", "Blush", ":$", [":$"], dc => FaceBase(dc, (c) => {
            Blush(c); Eyes(c, 0, 2); SmallSmile(c);
        })),
        Make("love", "Love", "<3", ["<3"], Heart),
        Make("sad", "Sad", ":(", [":(", ":-("], dc => FaceBase(dc, (c) => {
            Eyes(c, 0, 2); ArcMouth(c, true);
        })),
        Make("cry", "Cry", ":'(", [":'("], dc => FaceBase(dc, (c) => {
            Eyes(c, 0, 2); Tear(c); ArcMouth(c, true);
        })),
        Make("angry", "Angry", ":@", [":@", ">:("], dc => FaceBase(dc, (c) => {
            AngryBrows(c); Eyes(c, 0, 1); ArcMouth(c, true);
        })),
        Make("shock", "Shock", ":O", [":O", ":-O", ":o"], dc => FaceBase(dc, (c) => {
            Eyes(c, 0, -1, 5.2); ShockMouth(c);
        })),
        Make("think", "Think", ":/", [":/", ":-/"], dc => FaceBase(dc, (c) => {
            Eyes(c, 1, 0); ThinkMouth(c);
        })),
        Make("sleepy", "Sleepy", "|)", ["|)", "zzz"], dc => FaceBase(dc, (c) => {
            SleepEyes(c); SmallSmile(c); Zzz(c);
        })),
        Make("devil", "Devil", "]:)", ["]:)"], dc => {
            Horns(dc); FaceBase(dc, (c) => { Eyes(c, 0, 0); ArcMouth(c, false); });
        }),
        Make("angel", "Angel", "O:)", ["O:)", "O:-)"], dc => {
            Halo(dc); FaceBase(dc, (c) => { Eyes(c, 0, 0); SmallSmile(c); });
        }),
        Make("sick", "Sick", ":X", [":X", ":-X"], dc => FaceBase(dc, (c) => {
            GreenTint(c); Eyes(c, 0, 2); SickMouth(c);
        })),
        Make("fire", "Fire", ":fire:", [":fire:"], Fire),
        Make("star", "Star", ":star:", [":star:"], Star),
        Make("thumb", "Thumb", ":thumb:", [":thumb:"], Thumb),
        Make("clap", "Clap", ":clap:", [":clap:"], Clap),
        Make("party", "Party", ":party:", [":party:"], Party),
        Make("flower", "850", ":850:", [":850:"], Flower)
    ];

    private static Smile Make(string id, string name, string shortcut, string[] shortcuts, Action<DrawingContext> draw)
    {
        var img = Render(draw);
        return new Smile
        {
            Id = id,
            Name = name,
            Token = ":" + id + ":",
            Shortcut = shortcut,
            Shortcuts = shortcuts.OrderByDescending(s => s.Length).ToArray(),
            Image = img
        };
    }

    private static ImageSource Render(Action<DrawingContext> draw)
    {
        const int s = 64;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            draw(dc);
        var bmp = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private static Brush FaceBrush()
    {
        var g = new RadialGradientBrush(Face, FaceDeep) { Center = new Point(0.38, 0.32), RadiusX = 0.72, RadiusY = 0.72 };
        g.Freeze();
        return g;
    }

    private static Pen Stroke(double t = 2.2) => new(new SolidColorBrush(Line), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    private static void FaceBase(DrawingContext dc, Action<DrawingContext> features)
    {
        dc.DrawEllipse(FaceBrush(), Stroke(2.4), new Point(32, 34), 22, 22);
        features(dc);
    }

    private static void Eyes(DrawingContext dc, double ox, double oy, double r = 3.1)
    {
        EyeDot(dc, 23 + ox, 28 + oy, r);
        EyeDot(dc, 41 + ox, 28 + oy, r);
    }

    private static void EyeDot(DrawingContext dc, double x, double y, double r = 3.1)
    {
        dc.DrawEllipse(new SolidColorBrush(Eye), null, new Point(x, y), r, r);
        dc.DrawEllipse(new SolidColorBrush(White), null, new Point(x - 0.8, y - 0.9), r * 0.32, r * 0.32);
    }

    private static void Wink(DrawingContext dc, double x, double y)
    {
        var p = new Pen(new SolidColorBrush(Eye), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(p, new Point(x - 5, y), new Point(x + 5, y - 1.5));
    }

    private static void HappyEyes(DrawingContext dc)
    {
        var p = Stroke(2.3);
        dc.DrawGeometry(null, p, Arc(23, 29, 6, 200, 340));
        dc.DrawGeometry(null, p, Arc(41, 29, 6, 200, 340));
    }

    private static void SleepEyes(DrawingContext dc)
    {
        var p = new Pen(new SolidColorBrush(Eye), 2.2) { StartLineCap = PenLineCap.Round };
        dc.DrawLine(p, new Point(18, 29), new Point(27, 29));
        dc.DrawLine(p, new Point(37, 29), new Point(46, 29));
    }

    private static void ArcMouth(DrawingContext dc, bool sad)
    {
        var geo = sad ? Arc(32, 46, 9, 200, 340) : Arc(32, 38, 10, 20, 160);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Mouth), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }

    private static void SmallSmile(DrawingContext dc)
    {
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Mouth), 2.3) { StartLineCap = PenLineCap.Round }, Arc(32, 40, 7, 25, 155));
    }

    private static void OpenMouth(DrawingContext dc, bool grin)
    {
        var fill = new SolidColorBrush(Mouth);
        dc.DrawEllipse(fill, null, new Point(32, 44), grin ? 9 : 6, grin ? 7 : 6);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(80, 16, 16)), null, new Point(32, 46), 5, 3.2);
    }

    private static void ShockMouth(DrawingContext dc) =>
        dc.DrawEllipse(new SolidColorBrush(Mouth), null, new Point(32, 44), 5.5, 7);

    private static void ThinkMouth(DrawingContext dc)
    {
        var p = new Pen(new SolidColorBrush(Mouth), 2.4) { StartLineCap = PenLineCap.Round };
        dc.DrawLine(p, new Point(24, 44), new Point(42, 40));
    }

    private static void SickMouth(DrawingContext dc)
    {
        var p = new Pen(new SolidColorBrush(Color.FromRgb(60, 120, 70)), 2.4) { StartLineCap = PenLineCap.Round };
        dc.DrawLine(p, new Point(24, 42), new Point(28, 46));
        dc.DrawLine(p, new Point(28, 46), new Point(32, 42));
        dc.DrawLine(p, new Point(32, 42), new Point(36, 46));
        dc.DrawLine(p, new Point(36, 46), new Point(40, 42));
    }

    private static void Tongue(DrawingContext dc)
    {
        dc.DrawLine(new Pen(new SolidColorBrush(Eye), 2.2) { StartLineCap = PenLineCap.Round }, new Point(22, 42), new Point(42, 42));
        var t = new EllipseGeometry(new Point(36, 48), 5, 7);
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(255, 110, 130)), null, t);
    }

    private static void Kiss(DrawingContext dc)
    {
        var p = new Pen(new SolidColorBrush(Mouth), 2.2);
        dc.DrawGeometry(null, p, Arc(32, 42, 4.5, 200, 340));
        dc.DrawGeometry(null, p, Arc(32, 46, 4.5, 20, 160));
    }

    private static void Shades(DrawingContext dc)
    {
        var g = new SolidColorBrush(Color.FromRgb(28, 32, 36));
        dc.DrawRoundedRectangle(g, null, new Rect(14, 24, 16, 10), 3, 3);
        dc.DrawRoundedRectangle(g, null, new Rect(34, 24, 16, 10), 3, 3);
        dc.DrawRectangle(g, null, new Rect(29, 27, 6, 3));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(80, 80, 200, 255)), null, new Point(20, 27), 3, 2);
    }

    private static void Blush(DrawingContext dc)
    {
        var pink = new SolidColorBrush(Color.FromArgb(140, 255, 120, 140));
        dc.DrawEllipse(pink, null, new Point(18, 38), 6, 3.5);
        dc.DrawEllipse(pink, null, new Point(46, 38), 6, 3.5);
    }

    private static void Tear(DrawingContext dc)
    {
        var b = new SolidColorBrush(Color.FromRgb(80, 170, 230));
        dc.DrawEllipse(b, null, new Point(22, 36), 2.4, 3.6);
        dc.DrawEllipse(b, null, new Point(22, 42), 2.2, 3.2);
    }

    private static void AngryBrows(DrawingContext dc)
    {
        var p = new Pen(new SolidColorBrush(Eye), 2.4) { StartLineCap = PenLineCap.Round };
        dc.DrawLine(p, new Point(16, 20), new Point(28, 25));
        dc.DrawLine(p, new Point(48, 20), new Point(36, 25));
    }

    private static void Zzz(DrawingContext dc)
    {
        var f = new Typeface("Segoe UI");
        var ft = new FormattedText("z", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, f, 11, new SolidColorBrush(Line), 1.25);
        dc.DrawText(ft, new Point(46, 10));
    }

    private static void Horns(DrawingContext dc)
    {
        var b = new SolidColorBrush(Color.FromRgb(160, 40, 40));
        dc.DrawGeometry(b, Stroke(1.4), Triangle(18, 18, 24, 8, 28, 20));
        dc.DrawGeometry(b, Stroke(1.4), Triangle(46, 18, 40, 8, 36, 20));
    }

    private static void Halo(DrawingContext dc)
    {
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(255, 220, 80)), 2.6), new Point(32, 12), 14, 4);
    }

    private static void GreenTint(DrawingContext dc)
    {
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 160, 210, 90)), null, new Point(32, 34), 22, 22);
    }

    private static void Heart(DrawingContext dc)
    {
        var b = new SolidColorBrush(Color.FromRgb(220, 50, 70));
        dc.DrawEllipse(b, null, new Point(24, 28), 10, 10);
        dc.DrawEllipse(b, null, new Point(40, 28), 10, 10);
        dc.DrawGeometry(b, null, Triangle(14, 32, 50, 32, 32, 54));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, new Point(22, 24), 3, 2);
    }

    private static void Fire(DrawingContext dc)
    {
        var o = new SolidColorBrush(Color.FromRgb(230, 90, 20));
        var y = new SolidColorBrush(Color.FromRgb(255, 200, 60));
        dc.DrawGeometry(o, null, Flame(32, 54, 18));
        dc.DrawGeometry(y, null, Flame(32, 50, 10));
    }

    private static void Star(DrawingContext dc)
    {
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(255, 200, 40)), Stroke(1.2), StarGeo(32, 32, 18, 8, 5));
    }

    private static void Thumb(DrawingContext dc)
    {
        var skin = new SolidColorBrush(Face);
        dc.DrawRoundedRectangle(skin, Stroke(1.8), new Rect(18, 28, 28, 22), 6, 6);
        dc.DrawRoundedRectangle(skin, Stroke(1.8), new Rect(22, 12, 12, 22), 5, 5);
    }

    private static void Clap(DrawingContext dc)
    {
        var skin = new SolidColorBrush(Face);
        dc.DrawEllipse(skin, Stroke(1.8), new Point(24, 34), 12, 16);
        dc.DrawEllipse(skin, Stroke(1.8), new Point(40, 34), 12, 16);
    }

    private static void Party(DrawingContext dc)
    {
        FaceBase(dc, c => { Eyes(c, 0, 0); ArcMouth(c, false); });
        dc.DrawGeometry(new SolidColorBrush(Green), null, Triangle(20, 8, 44, 8, 32, 22));
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 80, 120)), null, new Point(14, 18), 3, 3);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(80, 160, 255)), null, new Point(50, 16), 3, 3);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 200, 40)), null, new Point(48, 48), 2.5, 2.5);
    }

    private static void Flower(DrawingContext dc)
    {
        var petals = new[]
        {
            new Point(32, 18), new Point(46, 26), new Point(44, 42), new Point(20, 42), new Point(18, 26)
        };
        foreach (var p in petals)
            dc.DrawEllipse(new SolidColorBrush(Green) { Opacity = 0.85 }, null, p, 10, 10);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(180, 255, 160)), null, new Point(32, 32), 7, 7);
    }

    private static StreamGeometry Arc(double cx, double cy, double r, double a0, double a1)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        var p0 = Polar(cx, cy, r, a0);
        var p1 = Polar(cx, cy, r, a1);
        gc.BeginFigure(p0, false, false);
        gc.ArcTo(p1, new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, true);
        g.Freeze();
        return g;
    }

    private static Point Polar(double cx, double cy, double r, double deg)
    {
        var a = deg * Math.PI / 180;
        return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }

    private static StreamGeometry Triangle(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        gc.BeginFigure(new Point(x1, y1), true, true);
        gc.LineTo(new Point(x2, y2), true, true);
        gc.LineTo(new Point(x3, y3), true, true);
        g.Freeze();
        return g;
    }

    private static StreamGeometry Flame(double x, double y, double w)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        gc.BeginFigure(new Point(x, y - w * 2.2), true, true);
        gc.BezierTo(new Point(x + w, y - w), new Point(x + w * 0.7, y), new Point(x, y), true, true);
        gc.BezierTo(new Point(x - w * 0.7, y), new Point(x - w, y - w), new Point(x, y - w * 2.2), true, true);
        g.Freeze();
        return g;
    }

    private static StreamGeometry StarGeo(double cx, double cy, double rOut, double rIn, int n)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        for (var i = 0; i < n * 2; i++)
        {
            var r = i % 2 == 0 ? rOut : rIn;
            var a = -90 + i * 180.0 / n;
            var p = Polar(cx, cy, r, a);
            if (i == 0) gc.BeginFigure(p, true, true);
            else gc.LineTo(p, true, true);
        }
        g.Freeze();
        return g;
    }
}
