using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Area850;

public partial class App : Application
{
    public static string ServerUrl { get; set; } = "https://icq.area850.com";
    public const string PublicUrl = "https://icq.area850.com";
    public const string LocalUrl = "http://127.0.0.1:8500";
    public static string Token { get; set; } = "";
    public static long UserId { get; set; }
    public static string Username { get; set; } = "";
    public static string Display { get; set; } = "";
    public static bool IsAdmin { get; set; }
    public static bool Invisible { get; set; }
    public static string Status { get; set; } = "online";
    public static bool AlwaysOnTop { get; set; }
    public static bool DarkMode { get; set; }

    private static string UiPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Area850", "ui.settings");

    public static void LoadUi()
    {
        try
        {
            if (!File.Exists(UiPath)) return;
            foreach (var line in File.ReadAllLines(UiPath))
            {
                if (line.StartsWith("alwaysontop=", StringComparison.OrdinalIgnoreCase))
                    AlwaysOnTop = line.EndsWith("1");
                if (line.StartsWith("dark=", StringComparison.OrdinalIgnoreCase))
                    DarkMode = line.EndsWith("1");
            }
        }
        catch { }
    }

    public static void SaveUi()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiPath)!);
            File.WriteAllText(UiPath, "alwaysontop=" + (AlwaysOnTop ? "1" : "0") + "\ndark=" + (DarkMode ? "1" : "0") + "\n");
        }
        catch { }
    }

    public static void ApplyTheme()
    {
        if (Current is null) return;
        if (DarkMode)
        {
            Set("Bg", "#0E1114");
            Set("Card", "#171B20");
            Set("Ink", "#E8ECF0");
            Set("Sub", "#8A9199");
            Set("Line", "#2A3038");
            Set("Mine", "#1C3A28");
            Set("Theirs", "#1E2329");
            Set("Hover", "#1E2329");
            Set("Select", "#1C3A28");
        }
        else
        {
            Set("Bg", "#F4F5F7");
            Set("Card", "#FFFFFF");
            Set("Ink", "#1C1F24");
            Set("Sub", "#8A9199");
            Set("Line", "#E6E8EC");
            Set("Mine", "#D8F8C8");
            Set("Theirs", "#F1F3F5");
            Set("Hover", "#F3F5F7");
            Set("Select", "#EEF8F1");
        }
    }

    public static void ToggleTheme()
    {
        DarkMode = !DarkMode;
        ApplyTheme();
        SaveUi();
    }

    private static void Set(string key, string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        Current.Resources[key] = new SolidColorBrush(c);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        LoadUi();
        base.OnStartup(e);
        ApplyTheme();
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "850 Messenger");
            args.Handled = true;
        };
    }
}
