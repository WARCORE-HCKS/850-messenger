using System.Text.Json;
using System.Windows;
using Area850.Services;

namespace Area850;

public partial class RoomSettingsWindow : Window
{
    private readonly HubClient _hub;
    private readonly string _slug;

    public RoomSettingsWindow(HubClient hub, string slug, JsonElement info)
    {
        InitializeComponent();
        _hub = hub;
        _slug = slug;
        Head.Text = info.GetString("title") ?? slug;
        TopicBox.Text = info.GetString("topic") ?? "";
        WelcomeBox.Text = info.GetString("welcome") ?? "";
        LockBox.IsChecked = info.TryGetProperty("doorLock", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
        SlowBox.IsChecked = info.TryGetProperty("slow", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? pass = PassBox.Password.Length == 0 ? null : PassBox.Password.Trim();
            await _hub.SetRoomMeta(_slug, TopicBox.Text, WelcomeBox.Text, pass, LockBox.IsChecked == true, SlowBox.IsChecked == true);
            Close();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
    }
}
