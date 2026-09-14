using System.Text.Json;
using System.Windows;
using Area850.Services;

namespace Area850;

public partial class ProfileWindow : Window
{
    private readonly HubClient _hub;
    private readonly bool _mine;

    public ProfileWindow(HubClient hub, JsonElement profile, bool mine)
    {
        InitializeComponent();
        _hub = hub;
        _mine = mine;
        var uin = profile.GetString("uin") ?? "";
        var display = profile.GetString("display") ?? "";
        TitleName.Text = display;
        UinLine.Text = "UIN  " + uin;
        StatusLine.Text = Label(profile.GetString("status") ?? "offline");
        NickBox.Text = display;
        MsgBox.Text = profile.GetString("statusMsg") ?? "";
        AboutBox.Text = profile.GetString("about") ?? "";
        CityBox.Text = profile.GetString("city") ?? "";
        GenderBox.Text = profile.GetString("gender") ?? "";
        if (mine)
        {
            Title = "My details";
            SaveBtn.Visibility = Visibility.Visible;
            NickBox.IsReadOnly = false;
            MsgBox.IsReadOnly = false;
            AboutBox.IsReadOnly = false;
            CityBox.IsReadOnly = false;
            GenderBox.IsReadOnly = false;
            TopBox.Visibility = Visibility.Visible;
            TopBox.IsChecked = App.AlwaysOnTop;
            DarkBox.Visibility = Visibility.Visible;
            DarkBox.IsChecked = App.DarkMode;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _hub.SetProfile(NickBox.Text.Trim(), AboutBox.Text, MsgBox.Text, CityBox.Text, GenderBox.Text);
            App.Display = NickBox.Text.Trim();
            App.AlwaysOnTop = TopBox.IsChecked == true;
            App.DarkMode = DarkBox.IsChecked == true;
            App.ApplyTheme();
            App.SaveUi();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "850");
        }
    }

    private void Dark_Toggle(object sender, RoutedEventArgs e)
    {
        if (!_mine) return;
        App.DarkMode = DarkBox.IsChecked == true;
        App.ApplyTheme();
        App.SaveUi();
    }

    private static string Label(string s) => s switch
    {
        "away" => "Away",
        "occupied" => "Occupied",
        "dnd" => "Do not disturb",
        "invisible" => "Invisible",
        "awaiting" => "Awaiting authorization",
        "offline" => "Offline",
        _ => "Online"
    };
}
