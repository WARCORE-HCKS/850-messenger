using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Area850.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Area850;

public partial class MainWindow : Window
{
    private readonly HubClient _hub = new();
    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, OutgoingFile> _outgoing = new();
    private readonly Dictionary<string, IncomingFile> _incoming = new();
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private string _room = "";
    private string? _peerUser;
    private bool _chatOpen;
    private bool _statusReady;
    private bool _autoAway;
    private bool _exit;
    private bool _typing;
    private DateTime _typedAt;
    private string _savedStatus = "online";
    private string _chatSubBase = "";
    private WinForms.NotifyIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Topmost = App.AlwaysOnTop;
        MeName.Text = App.Display;
        MeInitial.Text = (App.Display.Length > 0 ? App.Display[0] : '8').ToString().ToUpper();
        MeStatus.Text = "UIN " + App.Username + (App.IsAdmin ? "  ·  admin" : "");
        if (App.IsAdmin)
        {
            StatsBtn.Visibility = Visibility.Visible;
            GhostBtn.Visibility = Visibility.Visible;
        }
        PickStatus(App.Invisible ? "invisible" : "online", false);
        SearchBox.Text = "Search";
        SearchBox.GotFocus += (_, _) => { if (SearchBox.Text == "Search") SearchBox.Text = ""; };
        EmojiBank.Fill(EmojiWrap, Composer, EmojiPop);
        var listMenu = new ContextMenu();
        Menu(listMenu, "Add contact", () => AddFriend_Click(this, new RoutedEventArgs()));
        Menu(listMenu, "Chat rooms", () => Rooms_Click(this, new RoutedEventArgs()));
        People.ContextMenu = listMenu;
        ThemeIcon.Text = App.DarkMode ? "\uE706" : "\uE708";
        HookTray();
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();
        Loaded += async (_, _) => await Boot();
        Closing += OnClosing;
        Closed += async (_, _) =>
        {
            _idleTimer.Stop();
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            await _hub.DisposeAsync();
        };
    }

    private async Task Boot()
    {
        _hub.Message += m => Dispatcher.Invoke(() => AddMsg(m, false));
        _hub.History += (room, arr) => Dispatcher.Invoke(() =>
        {
            if (room != _room) return;
            Bubbles.Children.Clear();
            foreach (var item in arr.EnumerateArray()) AddMsg(item, true);
        });
        _hub.SystemLine += t => Dispatcher.Invoke(() =>
        {
            if (_chatOpen) Sys(t);
        });
        _hub.Presence += (u, d, s) => Dispatcher.Invoke(() => OnPresence(u, d, s));
        _hub.Nudged += who => Dispatcher.Invoke(() =>
        {
            if (Quiet) return;
            SoundBank.Nudge();
            Shake();
            if (_chatOpen) Sys(who + " sent a nudge.");
            Balloon(who + " sent a nudge.");
        });
        _hub.Whisper += w => Dispatcher.Invoke(() =>
        {
            var from = w.GetString("fromDisplay") ?? w.GetString("from") ?? "?";
            var body = w.GetString("body") ?? "";
            if (!Quiet) SoundBank.Message();
            if (_chatOpen) Sys("Whisper from " + from + ": " + body);
            Balloon("Whisper from " + from);
        });
        _hub.Signal += (user, display, kind, payload) => Dispatcher.Invoke(() =>
        {
            if (kind.StartsWith("room-")) return;
            if (kind is "offer-audio" or "offer-video")
            {
                if (MessageBox.Show($"{display} is calling. Answer?", "850 Call", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    new CallWindow(user, display, kind.Contains("video"), false, payload, _hub).Show();
            }
        });
        _hub.Friend += (u, d, st) => Dispatcher.Invoke(() => { UpsertPerson(u, d, st); PaintList(); });
        _hub.Unfriend += u => Dispatcher.Invoke(() =>
        {
            _rows.RemoveAll(r => r.Kind == "user" && r.Id.Equals(u, StringComparison.OrdinalIgnoreCase));
            PaintList();
        });
        _hub.AuthAsk += (u, d) => Dispatcher.Invoke(() => AskAuth(u, d));
        _hub.Kicked += (room, why) => Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(room)) return;
            MessageBox.Show(string.IsNullOrWhiteSpace(why) ? "Signed out by admin." : why, "850");
            _exit = true;
            Application.Current.Shutdown();
        });
        _hub.PeerTyping += (room, u, d, on) => Dispatcher.Invoke(() =>
        {
            if (room != _room) return;
            ChatSub.Text = on ? d + " is typing…" : _chatSubBase;
        });
        _hub.IncomingFile += (u, d, n, sz, id) => Dispatcher.Invoke(() => OnFileOffer(u, d, n, sz, id));
        _hub.FileAnswer += (u, id, ok) => Dispatcher.Invoke(() => OnFileDecide(u, id, ok));
        _hub.FilePart += (u, id, i, t, data) => Dispatcher.Invoke(() => OnFileChunk(u, id, i, t, data));
        try
        {
            await _hub.Connect(App.ServerUrl, App.Token);
            var who = await _hub.Who();
            if (who.Me is { IsAdmin: true }) App.IsAdmin = true;
            if (who.Me is { Invisible: true }) App.Invisible = true;
            if (App.IsAdmin)
            {
                StatsBtn.Visibility = Visibility.Visible;
                GhostBtn.Visibility = Visibility.Visible;
            }
            if (App.Invisible) PickStatus("invisible", false);
            ApplyGhostUi();
            foreach (var f in who.Friends)
                UpsertPerson(f.Username, string.IsNullOrEmpty(f.Display) ? f.Username : f.Display, f.Status);
            foreach (var p in who.Online)
            {
                if (p.Username.Equals(App.Username, StringComparison.OrdinalIgnoreCase)) continue;
                var row = _rows.FirstOrDefault(r => r.Kind == "user" && r.Id.Equals(p.Username, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                    UpsertPerson(p.Username, string.IsNullOrEmpty(p.Display) ? p.Username : p.Display, p.Ghost ? "invisible" : p.Status);
            }
            PaintList();
            foreach (var a in who.PendingAuth)
                AskAuth(a.From, a.Display);
            if (who.Mail.Count > 0)
            {
                foreach (var m in who.Mail)
                {
                    UpsertPerson(m.From, m.Display, "offline");
                    var row = _rows.FirstOrDefault(r => r.Id.Equals(m.From, StringComparison.OrdinalIgnoreCase));
                    if (row is not null) row.Unread = true;
                }
                PaintList();
                Balloon(who.Mail.Count + " offline message" + (who.Mail.Count == 1 ? "" : "s") + " waiting.");
                if (!Quiet) SoundBank.Message();
            }
            _statusReady = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Connected to login, but the messenger failed to load:\n" + ex.Message, "850 Messenger");
        }
    }

    private void OnPresence(string u, string d, string s)
    {
        var row = _rows.FirstOrDefault(r => r.Kind == "user" && r.Id.Equals(u, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        var wasOnline = row.Online;
        UpsertPerson(u, string.IsNullOrEmpty(d) ? row.Title : d, s);
        PaintList();
        if (s != "offline" && !wasOnline && !NoPop)
        {
            if (!Quiet) SoundBank.Online();
            Balloon(row.Title + " is online.");
        }
    }

    private void UpsertPerson(string user, string display, string status)
    {
        var row = _rows.FirstOrDefault(r => r.Kind == "user" && r.Id.Equals(user, StringComparison.OrdinalIgnoreCase));
        if (row is null) _rows.Add(new Row("user", user, display, status, IsLive(status)));
        else { row.Title = display; row.Sub = status; row.Online = IsLive(status); }
    }

    private static bool IsLive(string status) =>
        status is not "offline" and not "awaiting" and not "invisible";

    private void PaintList()
    {
        var q = (SearchBox.Text == "Search" ? "" : SearchBox.Text).Trim();
        People.Items.Clear();
        foreach (var r in _rows
                     .Where(x => x.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x => x.Sub == "awaiting" ? 0 : x.Online ? 1 : 2)
                     .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            People.Items.Add(new ListBoxItem
            {
                Content = BuildRow(r),
                Tag = r,
                ContextMenu = ContactMenu(r),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0)
            });
        }
    }

    private FrameworkElement BuildRow(Row r)
    {
        var grid = new Grid { Tag = r };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var av = new Grid { Width = 36, Height = 36 };
        var fill = ColorFromName(r.Title);
        av.Children.Add(new Ellipse { Fill = new SolidColorBrush(fill) });
        av.Children.Add(new TextBlock
        {
            Text = r.Title.Length > 0 ? r.Title[..1].ToUpper() : "#",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        av.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(StatusColor(r.Sub)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = r.Title,
            FontWeight = r.Unread ? FontWeights.Bold : FontWeights.SemiBold,
            FontSize = 13.5
        });
        text.Children.Add(new TextBlock
        {
            Text = "#" + r.Id + "  ·  " + StatusLabel(r.Sub),
            Foreground = (Brush)FindResource("Sub"),
            FontSize = 11.5
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(av);
        grid.Children.Add(text);
        return grid;
    }

    private ContextMenu ContactMenu(Row r)
    {
        var m = new ContextMenu();
        Menu(m, "Message", async () => await Open(r));
        Menu(m, "Nudge", async () =>
        {
            _peerUser = r.Id;
            SoundBank.Nudge();
            Shake();
            await _hub.Nudge(r.Id);
        });
        Menu(m, "Voice call", () => { _peerUser = r.Id; ChatTitle.Text = r.Title; StartCall(false); });
        Menu(m, "Video call", () => { _peerUser = r.Id; ChatTitle.Text = r.Title; StartCall(true); });
        Menu(m, "User details", async () => await ShowProfile(r.Id, false));
        Menu(m, "Send file", async () => { _peerUser = r.Id; await SendFile(); });
        Menu(m, "Copy UIN", () => Clipboard.SetText(r.Id));
        m.Items.Add(new Separator());
        Menu(m, "Block", async () =>
        {
            if (MessageBox.Show("Block #" + r.Id + "?", "850", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await _hub.Block(r.Id);
        });
        Menu(m, "Delete", async () =>
        {
            if (MessageBox.Show("Remove " + r.Title + " from your list?", "850", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await _hub.RemoveFriend(r.Id);
        });
        return m;
    }

    private static void Menu(ContextMenu m, string label, Action act)
    {
        var i = new MenuItem { Header = label };
        i.Click += (_, _) => act();
        m.Items.Add(i);
    }

    private async void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("UIN or nickname", "Add contact");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        try
        {
            if (name.All(char.IsDigit))
            {
                await _hub.AddFriend(name);
                return;
            }
            var found = await _hub.Find(name);
            if (found.ValueKind != JsonValueKind.Array || found.GetArrayLength() == 0)
            {
                MessageBox.Show("Nobody matches that name.", "850");
                return;
            }
            if (found.GetArrayLength() == 1)
            {
                await _hub.AddFriend(found[0].GetString("uin") ?? found[0].GetString("username") ?? name);
                return;
            }
            var pick = Prompt("Several people. Enter the UIN to add:\n" +
                              string.Join("\n", found.EnumerateArray().Select(x =>
                                  "#" + (x.GetString("uin") ?? "?") + "  " + (x.GetString("display") ?? ""))),
                "Find people");
            if (!string.IsNullOrWhiteSpace(pick)) await _hub.AddFriend(pick.Trim());
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
    }

    private async void AskAuth(string uin, string display)
    {
        SoundBank.Auth();
        var ok = MessageBox.Show(
            display + "  (#" + uin + ") wants to add you.\n\nAuthorize?",
            "Authorization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
        try { await _hub.AuthReply(uin, ok); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
    }

    private void Rooms_Click(object sender, RoutedEventArgs e)
    {
        new RoomsWindow(_hub) { Owner = this }.Show();
    }

    private async void People_Sel(object sender, SelectionChangedEventArgs e)
    {
        if (HitRow(People.SelectedItem) is not { } row || row.Kind != "user") return;
        await Open(row);
    }

    private void People_Right(object sender, MouseButtonEventArgs e)
    {
        var row = HitRowFromSource(e.OriginalSource);
        if (row is null) return;
        foreach (var o in People.Items)
        {
            if (HitRow(o) == row && o is ListBoxItem li)
            {
                People.SelectedItem = li;
                break;
            }
        }
    }

    private static Row? HitRow(object? item) => item switch
    {
        ListBoxItem { Tag: Row r } => r,
        FrameworkElement { Tag: Row r } => r,
        _ => null
    };

    private static Row? HitRowFromSource(object src)
    {
        for (var d = src as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: Row r }) return r;
        return null;
    }

    private async Task Open(Row row)
    {
        if (!string.IsNullOrEmpty(_room) && _room != row.Id) await _hub.LeaveRoom(_room);
        _room = row.Kind == "room" ? row.Id : "dm:" + string.Join(":", new[] { App.Username, row.Id }.OrderBy(x => x));
        _peerUser = row.Kind == "user" ? row.Id : null;
        ChatTitle.Text = row.Title;
        _chatSubBase = row.Kind == "room" ? "Public room" : StatusLabel(row.Sub);
        ChatSub.Text = _chatSubBase;
        row.Unread = false;
        PaintList();
        Bubbles.Children.Clear();
        await _hub.JoinRoom(_room);
        OpenChatPane();
        if (row.Kind == "user")
        {
            try
            {
                var p = await _hub.GetProfile(row.Id);
                var msg = p.GetString("statusMsg");
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    _chatSubBase = StatusLabel(row.Sub) + "  ·  " + msg;
                    ChatSub.Text = _chatSubBase;
                }
            }
            catch { /* details optional */ }
        }
    }

    private void OpenChatPane()
    {
        if (_chatOpen) return;
        _chatOpen = true;
        ListCol.Width = new GridLength(360);
        ChatCol.Width = new GridLength(1, GridUnitType.Star);
        Width = 820;
    }

    private void AddMsg(JsonElement m, bool hist)
    {
        var room = m.TryGetProperty("room", out var rr) ? rr.GetString() : _room;
        var display = m.GetString("display") ?? "?";
        var body = m.GetString("body") ?? "";
        var kind = m.GetString("kind") ?? "say";
        var mine = m.TryGetProperty("userId", out var id) && id.GetInt64() == App.UserId;
        var from = m.GetString("username") ?? "";

        if (!string.IsNullOrEmpty(room) && room != _room && !hist)
        {
            if (!mine && kind is "say" or "auto")
            {
                var peer = from;
                if (string.IsNullOrEmpty(peer) && room.StartsWith("dm:"))
                    peer = room[3..].Split(':').FirstOrDefault(x => !x.Equals(App.Username, StringComparison.OrdinalIgnoreCase)) ?? "";
                var row = _rows.FirstOrDefault(r => r.Id.Equals(peer, StringComparison.OrdinalIgnoreCase));
                if (row is not null) row.Unread = true;
                else if (!string.IsNullOrEmpty(peer)) UpsertPerson(peer, display, "online");
                PaintList();
                if (!Quiet) SoundBank.Message();
                if (!NoPop) Balloon("Message from " + display);
            }
            return;
        }

        if (kind == "say" && !mine && !hist && !Quiet) SoundBank.Message();
        if (kind != "say") { Sys((kind == "auto" ? "Away message — " : "") + display + ": " + body); return; }

        var align = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var bg = mine ? (Brush)FindResource("Mine") : (Brush)FindResource("Theirs");
        var stack = new StackPanel { HorizontalAlignment = align, MaxWidth = 340, Margin = new Thickness(0, 0, 0, 8) };
        if (!mine) stack.Children.Add(new TextBlock { Text = display, FontSize = 11, Foreground = (Brush)FindResource("Sub"), Margin = new Thickness(10, 0, 0, 2) });
        var bubble = new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 8, 12, 8),
            Child = EmojiBank.Message(body, (Brush)FindResource("Ink")),
            ContextMenu = EmojiBank.CopyMenu(body)
        };
        stack.Children.Add(bubble);
        Bubbles.Children.Add(stack);
        ChatScroll.ScrollToEnd();
    }

    private void Sys(string t)
    {
        Bubbles.Children.Add(new TextBlock
        {
            Text = t,
            Foreground = (Brush)FindResource("Sub"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });
        ChatScroll.ScrollToEnd();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await Send();
    private async void Composer_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await Send(); return; }
        await PulseTyping();
    }

    private async Task PulseTyping()
    {
        if (string.IsNullOrEmpty(_room)) return;
        _typedAt = DateTime.UtcNow;
        if (_typing) return;
        _typing = true;
        try { await _hub.Typing(_room, true); } catch { }
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            if ((DateTime.UtcNow - _typedAt).TotalMilliseconds < 1400) return;
            await Dispatcher.InvokeAsync(async () =>
            {
                _typing = false;
                try { await _hub.Typing(_room, false); } catch { }
            });
        });
    }

    private async Task Send()
    {
        var text = Composer.Text.Trim();
        if (text.Length == 0 || string.IsNullOrEmpty(_room)) return;
        Composer.Text = "";
        if (_typing)
        {
            _typing = false;
            try { await _hub.Typing(_room, false); } catch { }
        }
        text = EmojiBank.Expand(text);
        var w = System.Text.RegularExpressions.Regex.Match(text, @"^/w\s+(\S+)\s+(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (w.Success) { await _hub.WhisperTo(w.Groups[1].Value, EmojiBank.Expand(w.Groups[2].Value)); return; }
        await _hub.Send(_room, text);
    }

    private void Emoji_Click(object sender, RoutedEventArgs e) => EmojiPop.IsOpen = !EmojiPop.IsOpen;
    private async void Nudge_Click(object sender, RoutedEventArgs e)
    {
        SoundBank.Nudge();
        Shake();
        await _hub.Nudge(_peerUser);
    }

    private void Call_Click(object sender, RoutedEventArgs e) => StartCall(false);
    private void Video_Click(object sender, RoutedEventArgs e) => StartCall(true);

    private void StartCall(bool video)
    {
        if (_peerUser is null)
        {
            MessageBox.Show("Pick a person to call. Rooms are for chat — calls are 1:1 like ICQ.", "850");
            return;
        }
        SoundBank.Call();
        new CallWindow(_peerUser, ChatTitle.Text, video, true, null, _hub).Show();
    }

    private async void File_Click(object sender, RoutedEventArgs e) => await SendFile();

    private async Task SendFile()
    {
        if (_peerUser is null)
        {
            MessageBox.Show("Pick a contact to send a file to.", "850");
            return;
        }
        var dlg = new OpenFileDialog { Title = "Send file" };
        if (dlg.ShowDialog() != true) return;
        var info = new FileInfo(dlg.FileName);
        if (info.Length > 15L * 1024 * 1024)
        {
            MessageBox.Show("Keep it under 15 MB.", "850");
            return;
        }
        var id = Guid.NewGuid().ToString("N");
        _outgoing[id] = new OutgoingFile(_peerUser, dlg.FileName);
        try { await _hub.FileOffer(_peerUser, info.Name, info.Length, id); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
        if (_chatOpen) Sys("Offered " + info.Name + " (" + (info.Length / 1024) + " KB).");
    }

    private async void OnFileOffer(string user, string display, string name, long size, string id)
    {
        var ok = MessageBox.Show(
            display + " wants to send:\n" + name + "\n" + (size / 1024) + " KB\n\nAccept?",
            "File",
            MessageBoxButton.YesNo) == MessageBoxResult.Yes;
        if (!ok)
        {
            try { await _hub.FileDecide(user, id, false); } catch { }
            return;
        }
        var save = new SaveFileDialog { FileName = name, Title = "Save incoming file" };
        if (save.ShowDialog() != true)
        {
            try { await _hub.FileDecide(user, id, false); } catch { }
            return;
        }
        _incoming[id] = new IncomingFile(user, save.FileName);
        try { await _hub.FileDecide(user, id, true); } catch { }
    }

    private async void OnFileDecide(string user, string id, bool accept)
    {
        if (!_outgoing.TryGetValue(id, out var file)) return;
        if (!accept)
        {
            if (_chatOpen) Sys("File declined.");
            _outgoing.Remove(id);
            return;
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(file.Path);
            const int chunk = 24 * 1024;
            var total = Math.Max(1, (bytes.Length + chunk - 1) / chunk);
            for (var i = 0; i < total; i++)
            {
                var n = Math.Min(chunk, bytes.Length - i * chunk);
                var b64 = Convert.ToBase64String(bytes, i * chunk, n);
                await _hub.FileChunk(user, id, i, total, b64);
            }
            if (_chatOpen) Sys("File sent.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
        _outgoing.Remove(id);
    }

    private void OnFileChunk(string user, string id, int index, int total, string data)
    {
        if (!_incoming.TryGetValue(id, out var file)) return;
        if (total < 1) return;
        if (file.Parts.Length != total)
            file.Parts = new byte[total][];
        if (index < 0 || index >= total) return;
        if (file.Parts[index] is null) file.Got++;
        file.Parts[index] = Convert.FromBase64String(data);
        if (file.Got < total) return;
        try
        {
            using var fs = File.Create(file.Path);
            foreach (var p in file.Parts)
                if (p is not null) fs.Write(p, 0, p.Length);
            if (_chatOpen) Sys("Saved " + System.IO.Path.GetFileName(file.Path));
            Balloon("File received: " + System.IO.Path.GetFileName(file.Path));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
        _incoming.Remove(id);
    }

    private async void Ghost_Click(object sender, RoutedEventArgs e)
    {
        var next = App.Invisible ? "online" : "invisible";
        PickStatus(next, false);
        await ApplyStatusAsync(next);
    }

    private async void Status_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_statusReady) return;
        if (StatusPick.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag as string ?? "online";
        await ApplyStatusAsync(tag);
    }

    private async Task ApplyStatusAsync(string status)
    {
        App.Status = status;
        App.Invisible = status == "invisible";
        _autoAway = false;
        try { await _hub.SetStatus(status); } catch { }
        ApplyGhostUi();
    }

    private void PickStatus(string status, bool fire)
    {
        var ready = _statusReady;
        if (!fire) _statusReady = false;
        foreach (var o in StatusPick.Items)
        {
            if (o is ComboBoxItem c && (c.Tag as string) == status)
            {
                StatusPick.SelectedItem = c;
                break;
            }
        }
        App.Status = status;
        App.Invisible = status == "invisible";
        _statusReady = fire ? ready : ready;
        if (!fire) _statusReady = ready;
    }

    private void ApplyGhostUi()
    {
        MeName.Text = App.Display;
        MeStatus.Text = "UIN " + App.Username + (App.IsAdmin ? "  ·  admin" : "");
        GhostIcon.Foreground = App.Invisible
            ? new SolidColorBrush(Color.FromRgb(107, 47, 160))
            : (Brush)FindResource("Sub");
        Title = App.Invisible ? "850 Messenger  ·  invisible" : "850 Messenger";
        Topmost = App.AlwaysOnTop;
    }

    private void Stats_Click(object sender, RoutedEventArgs e)
    {
        if (!App.IsAdmin) return;
        new AdminWindow(_hub) { Owner = this }.Show();
    }

    private void Help_Click(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        App.ToggleTheme();
        ThemeIcon.Text = App.DarkMode ? "\uE706" : "\uE708";
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowProfile(App.Username, true);
            MeName.Text = App.Display;
            MeInitial.Text = (App.Display.Length > 0 ? App.Display[0] : '8').ToString().ToUpper();
            Topmost = App.AlwaysOnTop;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
    }

    private async Task ShowProfile(string uin, bool mine)
    {
        var p = await _hub.GetProfile(uin);
        new ProfileWindow(_hub, p, mine) { Owner = this }.ShowDialog();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => PaintList();

    private void Shake()
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(Left, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(Left + 8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(Left - 8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(Left, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        BeginAnimation(LeftProperty, a);
    }

    private string? Prompt(string label, string title)
    {
        var tb = new TextBox { Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "OK", Height = 34, IsDefault = true, Background = (Brush)FindResource("Accent"), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(tb);
        panel.Children.Add(ok);
        var w = new Window
        {
            Title = title,
            Width = 340,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("Bg"),
            Content = panel
        };
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; w.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = tb.Text; w.Close(); } };
        w.ShowDialog();
        return result;
    }

    private void HookTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "850 Messenger",
            Visible = true,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
                ?? System.Drawing.SystemIcons.Application
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new WinForms.ContextMenuStrip();
        void St(string label, string tag) => menu.Items.Add(label, null, async (_, _) =>
        {
            PickStatus(tag, false);
            await ApplyStatusAsync(tag);
        });
        St("Online", "online");
        St("Away", "away");
        St("Occupied", "occupied");
        St("Do not disturb", "dnd");
        St("Invisible", "invisible");
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _exit = true;
            Application.Current.Shutdown();
        });
        _tray.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exit) return;
        e.Cancel = true;
        Hide();
        Balloon("Still signed in. Double-click the tray flower to open.");
    }

    private void Balloon(string text)
    {
        try { _tray?.ShowBalloonTip(2500, "850 Messenger", text, WinForms.ToolTipIcon.Info); }
        catch { /* tray optional */ }
    }

    private void CheckIdle()
    {
        if (App.Status is not "online") return;
        if (IdleMs() < 5 * 60 * 1000) return;
        _savedStatus = "online";
        _autoAway = true;
        PickStatus("away", false);
        _ = ApplyStatusAsync("away");
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        WakeFromAway();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        WakeFromAway();
    }

    private void WakeFromAway()
    {
        if (!_autoAway) return;
        _autoAway = false;
        PickStatus(_savedStatus, false);
        _ = ApplyStatusAsync(_savedStatus);
    }

    private static uint IdleMs()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (uint)Environment.TickCount - info.Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    private bool Quiet => App.Status == "dnd";
    private bool NoPop => App.Status is "dnd" or "occupied";

    private static Color ColorFromName(string name)
    {
        int h = name.Aggregate(0, (a, c) => a * 33 + c);
        var pal = new[] { Color.FromRgb(39, 208, 108), Color.FromRgb(49, 106, 197), Color.FromRgb(196, 43, 28), Color.FromRgb(107, 47, 160), Color.FromRgb(11, 122, 143) };
        return pal[Math.Abs(h) % pal.Length];
    }

    private static Color StatusColor(string s) => s switch
    {
        "away" => Color.FromRgb(240, 176, 32),
        "occupied" => Color.FromRgb(196, 43, 28),
        "dnd" => Color.FromRgb(120, 20, 20),
        "invisible" => Color.FromRgb(107, 47, 160),
        "awaiting" => Color.FromRgb(160, 168, 176),
        "offline" => Color.FromRgb(176, 182, 188),
        _ => Color.FromRgb(39, 208, 108)
    };

    private static string StatusLabel(string s) => s switch
    {
        "away" => "Away",
        "occupied" => "Occupied",
        "dnd" => "Do not disturb",
        "invisible" => "Invisible",
        "awaiting" => "Awaiting authorization",
        "offline" => "Offline",
        _ => "Online"
    };

    private sealed class Row
    {
        public string Kind, Id, Title, Sub;
        public bool Online;
        public bool Unread;
        public Row(string k, string id, string t, string s, bool o) { Kind = k; Id = id; Title = t; Sub = s; Online = o; }
    }

    private sealed class OutgoingFile
    {
        public string To, Path;
        public OutgoingFile(string to, string path) { To = to; Path = path; }
    }

    private sealed class IncomingFile
    {
        public string From, Path;
        public int Got;
        public byte[][] Parts = new byte[1][];
        public IncomingFile(string from, string path) { From = from; Path = path; }
    }
}

internal static class JsonGet
{
    public static string? GetString(this JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.ToString(),
            _ => p.ToString()
        };
    }
}
