using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Area850.Services;

namespace Area850;

public partial class LoginWindow : Window
{
    private bool _register = true;
    private readonly Api _api = new();

    public LoginWindow()
    {
        InitializeComponent();
        DarkBox.IsChecked = App.DarkMode;
        PaintThemeIcon();
        Loaded += async (_, _) =>
        {
            TabIn_Click(this, new RoutedEventArgs());
            Err.Text = "Connecting…";
            var ok = await EnsureServerAsync();
            Err.Text = ok ? "" : "Can't reach the net. Close and open 850 Messenger again.";
        };
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        DarkBox.IsChecked = DarkBox.IsChecked != true;
    }

    private void Dark_Toggle(object sender, RoutedEventArgs e)
    {
        App.DarkMode = DarkBox.IsChecked == true;
        App.ApplyTheme();
        App.SaveUi();
        PaintThemeIcon();
        if (!IsLoaded) return;
        if (_register) TabUp_Click(this, new RoutedEventArgs());
        else TabIn_Click(this, new RoutedEventArgs());
    }

    private void PaintThemeIcon() =>
        ThemeIcon.Text = App.DarkMode ? "\uE706" : "\uE708";

    private void TabIn_Click(object sender, RoutedEventArgs e)
    {
        _register = false;
        TabIn.FontWeight = FontWeights.SemiBold; TabIn.Foreground = FindResource("Ink") as System.Windows.Media.Brush;
        TabUp.FontWeight = FontWeights.Normal; TabUp.Foreground = FindResource("Sub") as System.Windows.Media.Brush;
        DisplayRow.Visibility = Visibility.Collapsed;
        GhostBox.Visibility = Visibility.Visible;
        IdLabel.Text = "UIN (your number)";
        UserBox.Visibility = Visibility.Visible;
        UserBox.IsEnabled = true;
    }

    private void TabUp_Click(object sender, RoutedEventArgs e)
    {
        _register = true;
        TabUp.FontWeight = FontWeights.SemiBold; TabUp.Foreground = FindResource("Ink") as System.Windows.Media.Brush;
        TabIn.FontWeight = FontWeights.Normal; TabIn.Foreground = FindResource("Sub") as System.Windows.Media.Brush;
        DisplayRow.Visibility = Visibility.Visible;
        GhostBox.Visibility = Visibility.Collapsed;
        GhostBox.IsChecked = false;
        IdLabel.Text = "You'll get a number like ICQ — write it down.";
        UserBox.Visibility = Visibility.Collapsed;
        UserBox.Text = "";
    }

    private void Help_Click(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        Err.Text = "";
        if (!await EnsureServerAsync())
        {
            Err.Text = "Can't reach the net. Try again.";
            return;
        }
        _api.Base = App.ServerUrl;
        var user = UserBox.Text.Trim();
        var pass = PassBox.Password;
        var display = string.IsNullOrWhiteSpace(DisplayBox.Text) ? user : DisplayBox.Text.Trim();
        var (ok, error, data) = _register
            ? await _api.Register(pass, display)
            : await _api.Login(user, pass, GhostBox.IsChecked == true);
        if (!ok || data is null) { Err.Text = error; return; }
        if (_register)
        {
            MessageBox.Show(
                $"Your number is {data.Uin}\n\nThis is how you sign in — like an ICQ UIN. Write it down.",
                "850 Messenger",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        App.Token = data.Token;
        App.UserId = data.UserId;
        App.Username = data.Username;
        App.Display = data.Display;
        App.IsAdmin = data.IsAdmin;
        App.Invisible = data.Invisible;
        App.Status = data.Invisible ? "invisible" : "online";
        SoundBank.SignOn();
        try
        {
            var main = new MainWindow();
            main.Show();
            Close();
        }
        catch (Exception ex)
        {
            Err.Text = "Signed in, but the window failed: " + ex.Message;
        }
    }

    private static async Task<bool> EnsureServerAsync()
    {
        if (await Ping(App.PublicUrl))
        {
            App.ServerUrl = App.PublicUrl;
            return true;
        }
        if (await Ping(App.LocalUrl))
        {
            App.ServerUrl = App.LocalUrl;
            return true;
        }
        StartServerProcess();
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            if (await Ping(App.LocalUrl))
            {
                App.ServerUrl = App.LocalUrl;
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> Ping(string baseUrl)
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var r = await c.GetAsync(baseUrl.TrimEnd('/') + "/api/info");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static void StartServerProcess()
    {
        var server = Path.Combine(AppContext.BaseDirectory, "Area850.Server.exe");
        var alt = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\Server\bin\Release\net8.0-windows\Area850.Server.exe"));
        var path = File.Exists(server) ? server : alt;
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)
        });
    }
}
