using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Area850.Services;

namespace Area850;

public partial class AdminWindow : Window
{
    private readonly HubClient _hub;

    public AdminWindow(HubClient hub)
    {
        InitializeComponent();
        _hub = hub;
        Loaded += async (_, _) => await LoadOverview();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedIndex == 0) await LoadOverview();
        else if (Tabs.SelectedIndex == 1) await LoadUsers();
        else if (Tabs.SelectedIndex == 2) await LoadRooms();
        else await LoadMsgs();
    }

    private async void Tabs_Sel(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        try
        {
            if (Tabs.SelectedIndex == 0) await LoadOverview();
            else if (Tabs.SelectedIndex == 1) await LoadUsers();
            else if (Tabs.SelectedIndex == 2) await LoadRooms();
            else if (Tabs.SelectedIndex == 3) await LoadMsgs();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
    }

    private async Task LoadOverview()
    {
        var s = await _hub.AdminOverview();
        NUsers.Text = Num(s, "users").ToString();
        NRooms.Text = Num(s, "rooms").ToString();
        NToday.Text = Num(s, "today").ToString();
        if (s.TryGetProperty("live", out var live))
        {
            NOnline.Text = Num(live, "online").ToString();
            NGhosts.Text = Num(live, "ghosts").ToString();
            SessionList.Items.Clear();
            if (live.TryGetProperty("sessions", out var sess))
                foreach (var p in sess.EnumerateArray())
                    SessionList.Items.Add(new Row
                    {
                        Uin = p.GetString("username") ?? "",
                        Name = p.GetString("display") ?? "",
                        Status = (p.TryGetProperty("invisible", out var g) && g.ValueKind == JsonValueKind.True) ? "ghost" : (p.GetString("status") ?? "")
                    });
        }
    }

    private async Task LoadUsers()
    {
        var arr = await _hub.AdminUsers(UserQ.Text.Trim());
        UserList.Items.Clear();
        foreach (var u in arr.EnumerateArray())
            UserList.Items.Add(new Row
            {
                Uin = u.GetString("uin") ?? "",
                Name = u.GetString("display") ?? "",
                Flags = (u.TryGetProperty("isAdmin", out var a) && a.ValueKind == JsonValueKind.True ? "admin " : "")
                      + (u.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True ? "disabled" : ""),
                Created = (u.GetString("created") ?? "")[..Math.Min(10, (u.GetString("created") ?? "").Length)],
                Id = u.GetString("username") ?? ""
            });
    }

    private async Task LoadRooms()
    {
        var arr = await _hub.AdminRooms();
        RoomList.Items.Clear();
        foreach (var r in arr.EnumerateArray())
            RoomList.Items.Add(new Row
            {
                Id = r.GetString("slug") ?? "",
                Title = r.GetString("title") ?? "",
                Channel = r.GetString("channel") ?? "",
                Users = r.TryGetProperty("users", out var n) && n.TryGetInt32(out var i) ? i.ToString() : "0",
                Messages = r.TryGetProperty("messages", out var m) && m.TryGetInt64(out var c) ? c.ToString() : "0",
                Flags = (r.TryGetProperty("locked", out var l) && l.ValueKind == JsonValueKind.True ? "password " : "")
                      + (r.TryGetProperty("doorLock", out var dl) && dl.ValueKind == JsonValueKind.True ? "locked " : "")
                      + (r.TryGetProperty("slow", out var sl) && sl.ValueKind == JsonValueKind.True ? "slow" : "")
            });
    }

    private async Task LoadMsgs()
    {
        var arr = await _hub.AdminMessages(string.IsNullOrWhiteSpace(MsgQ.Text) ? null : MsgQ.Text.Trim(), 120);
        MsgList.Items.Clear();
        foreach (var m in arr.EnumerateArray())
        {
            var at = (m.GetString("at") ?? "").Replace("T", " ");
            if (at.Length > 19) at = at[..19];
            MsgList.Items.Add(new Row
            {
                At = at,
                Room = m.GetString("room") ?? "",
                Who = m.GetString("display") ?? "",
                Body = m.GetString("body") ?? ""
            });
        }
    }

    private async void KickSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not Row r) return;
        await Run(() => _hub.AdminKickUser(r.Uin));
        await LoadOverview();
    }

    private async void SearchUsers_Click(object sender, RoutedEventArgs e) => await LoadUsers();
    private async void UserQ_Key(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await LoadUsers(); } }
    private async void LoadMsgs_Click(object sender, RoutedEventArgs e) => await LoadMsgs();
    private async void MsgQ_Key(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await LoadMsgs(); } }

    private async void UserPass_Click(object sender, RoutedEventArgs e)
    {
        if (UserSel() is not { } u) return;
        var pw = Ask("New password for #" + u);
        if (string.IsNullOrEmpty(pw)) return;
        await Run(() => _hub.AdminSetPassword(u, pw));
    }

    private async void UserKick_Click(object sender, RoutedEventArgs e)
    {
        if (UserSel() is not { } u) return;
        await Run(() => _hub.AdminKickUser(u));
        await LoadUsers();
        await LoadOverview();
    }

    private async void UserDisable_Click(object sender, RoutedEventArgs e)
    {
        if (UserSel() is not { } u) return;
        await Run(() => _hub.AdminDisable(u, true));
        await LoadUsers();
    }

    private async void UserEnable_Click(object sender, RoutedEventArgs e)
    {
        if (UserSel() is not { } u) return;
        await Run(() => _hub.AdminDisable(u, false));
        await LoadUsers();
    }

    private async void UserDelete_Click(object sender, RoutedEventArgs e)
    {
        if (UserSel() is not { } u) return;
        if (MessageBox.Show("Delete #" + u + " forever?", "850", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Run(() => _hub.AdminDeleteUser(u));
        await LoadUsers();
    }

    private async void RoomSay_Click(object sender, RoutedEventArgs e)
    {
        if (RoomSel() is not { } slug) return;
        var body = Ask("Announce to /" + slug);
        if (string.IsNullOrWhiteSpace(body)) return;
        await Run(() => _hub.AdminSay(slug, body));
    }

    private async void RoomLock_Click(object sender, RoutedEventArgs e)
    {
        if (RoomSel() is not { } slug) return;
        await Run(() => _hub.AdminLockRoom(slug, true));
        await LoadRooms();
    }

    private async void RoomUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (RoomSel() is not { } slug) return;
        await Run(() => _hub.AdminLockRoom(slug, false));
        await LoadRooms();
    }

    private async void RoomPass_Click(object sender, RoutedEventArgs e)
    {
        if (RoomSel() is not { } slug) return;
        await Run(() => _hub.AdminClearPass(slug));
        await LoadRooms();
    }

    private async void RoomDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RoomSel() is not { } slug) return;
        if (MessageBox.Show("Delete room " + slug + "?", "850", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Run(() => _hub.AdminDeleteRoom(slug));
        await LoadRooms();
    }

    private string? UserSel() => UserList.SelectedItem is Row r ? (string.IsNullOrEmpty(r.Id) ? r.Uin : r.Id) : null;
    private string? RoomSel() => RoomList.SelectedItem is Row r ? r.Id : null;

    private async Task Run(Func<Task> fn)
    {
        try { await fn(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
    }

    private string? Ask(string label)
    {
        var tb = new TextBox { Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "OK", Height = 34, IsDefault = true, Background = (System.Windows.Media.Brush)FindResource("Accent"), Foreground = System.Windows.Media.Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(tb);
        panel.Children.Add(ok);
        var w = new Window
        {
            Title = "850",
            Width = 320,
            Height = 160,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("Bg"),
            Content = panel
        };
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; w.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = tb.Text; w.Close(); } };
        w.ShowDialog();
        return result;
    }

    private static long Num(JsonElement e, string n) =>
        e.TryGetProperty(n, out var p) && p.TryGetInt64(out var v) ? v : 0;

    private sealed class Row
    {
        public string Uin { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Flags { get; set; } = "";
        public string Created { get; set; } = "";
        public string Title { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Users { get; set; } = "";
        public string Messages { get; set; } = "";
        public string At { get; set; } = "";
        public string Room { get; set; } = "";
        public string Who { get; set; } = "";
        public string Body { get; set; } = "";
    }
}
