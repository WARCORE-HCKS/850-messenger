using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Area850.Services;

namespace Area850;

public partial class RoomsWindow : Window
{
    private readonly HubClient _hub;
    private List<RoomDto> _rooms = new();
    private string _channel = "hangout";

    public RoomsWindow(HubClient hub)
    {
        InitializeComponent();
        _hub = hub;
        foreach (var (id, label) in CreateRoomWindow.Channels)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = id };
            row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(ChanColor(id)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Chans.Items.Add(new ListBoxItem { Content = row, Tag = id, Padding = new Thickness(12, 8, 12, 8) });
        }
        Chans.SelectedIndex = 0;
        Loaded += async (_, _) => await Reload();
    }

    private async Task Reload()
    {
        var who = await _hub.Who();
        _rooms = who.Rooms;
        PaintChans();
        PaintRooms();
    }

    private void Chan_Sel(object sender, SelectionChangedEventArgs e)
    {
        if (Chans.SelectedItem is ListBoxItem { Tag: string id })
        {
            _channel = id;
            var label = CreateRoomWindow.Channels.First(c => c.id == id).label;
            ChanTitle.Text = label;
            ChanSub.Text = "Double-click to join. 🔒 needs a password.";
            PaintRooms();
        }
    }

    private void PaintChans()
    {
        foreach (var o in Chans.Items)
        {
            if (o is not ListBoxItem { Tag: string id, Content: StackPanel sp }) continue;
            var n = _rooms.Where(r => ChanOf(r) == id).Sum(r => r.Users);
            if (sp.Children.Count == 2)
                sp.Children.Add(new TextBlock { Text = n > 0 ? n.ToString() : "", Foreground = (Brush)FindResource("Sub"), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            else if (sp.Children[2] is TextBlock tb)
                tb.Text = n > 0 ? n.ToString() : "";
        }
    }

    private void PaintRooms()
    {
        List.Items.Clear();
        foreach (var r in _rooms.Where(x => ChanOf(x) == _channel).OrderByDescending(x => x.Hot).ThenByDescending(x => x.Users).ThenBy(x => x.Title))
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Margin = new Thickness(10, 8, 8, 8) };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = r.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            if (r.Locked) title.Children.Add(new TextBlock { Text = "  🔒", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            if (r.DoorLock) title.Children.Add(new TextBlock { Text = "  door locked", FontSize = 11, Foreground = (Brush)FindResource("Sub"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            if (r.Hot) title.Children.Add(new TextBlock { Text = "  ● live", FontSize = 11, Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            left.Children.Add(title);
            var sub = string.IsNullOrWhiteSpace(r.Topic) ? "Owner #" + (string.IsNullOrEmpty(r.Owner) ? "0" : r.Owner) : r.Topic;
            left.Children.Add(new TextBlock { Text = sub, Foreground = (Brush)FindResource("Sub"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
            var count = new TextBlock
            {
                Text = r.Users == 0 ? "" : r.Users + (r.Users == 1 ? " in" : " in"),
                Foreground = (Brush)FindResource("Sub"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(count, 1);
            grid.Children.Add(left);
            grid.Children.Add(count);
            var item = new ListBoxItem { Content = grid, Tag = r, ContextMenu = RoomMenu(r) };
            List.Items.Add(item);
        }
        if (List.Items.Count == 0)
            List.Items.Add(new ListBoxItem { Content = "Empty channel. Make a room.", IsEnabled = false, Foreground = (Brush)FindResource("Sub") });
    }

    private ContextMenu RoomMenu(RoomDto r)
    {
        var m = new ContextMenu();
        var join = new MenuItem { Header = r.Locked ? "Join (password)" : "Join" };
        join.Click += (_, _) => Open(r);
        m.Items.Add(join);
        return m;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var w = new CreateRoomWindow(_hub) { Owner = this };
        if (w.ShowDialog() == true && !string.IsNullOrEmpty(w.CreatedSlug))
        {
            await Reload();
            new RoomChatWindow(_hub, w.CreatedSlug, w.CreatedTitle ?? "Room") { Owner = Owner }.Show();
            Close();
        }
    }

    private void Join_Click(object sender, MouseButtonEventArgs e)
    {
        if (Current() is { } r) Open(r);
    }

    private void List_Right(object sender, MouseButtonEventArgs e)
    {
        /* item context menu */
    }

    private RoomDto? Current() => List.SelectedItem is ListBoxItem { Tag: RoomDto r } ? r : null;

    private void Open(RoomDto r)
    {
        string? pass = null;
        if (r.Locked)
        {
            pass = AskPass(r.Title);
            if (pass is null) return;
        }
        new RoomChatWindow(_hub, r.Slug, r.Title, pass) { Owner = Owner }.Show();
        Close();
    }

    private string? AskPass(string title)
    {
        var pb = new PasswordBox { Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "Enter", Height = 34, IsDefault = true, Background = (Brush)FindResource("Accent"), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = title + " is password locked." });
        panel.Children.Add(pb);
        panel.Children.Add(ok);
        var w = new Window
        {
            Title = "Password",
            Width = 300,
            Height = 160,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("Bg"),
            Content = panel
        };
        string? result = null;
        ok.Click += (_, _) => { result = pb.Password; w.Close(); };
        pb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = pb.Password; w.Close(); } };
        w.ShowDialog();
        return result;
    }

    private static string ChanOf(RoomDto r) =>
        string.IsNullOrWhiteSpace(r.Channel) ? (string.IsNullOrWhiteSpace(r.Kind) || r.Kind == "public" ? "hangout" : r.Kind) : r.Channel;

    private static Color ChanColor(string id) => id switch
    {
        "music" => Color.FromRgb(196, 43, 160),
        "tech" => Color.FromRgb(11, 122, 143),
        "gaming" => Color.FromRgb(49, 106, 197),
        "dating" => Color.FromRgb(196, 43, 28),
        "afterdark" => Color.FromRgb(107, 47, 160),
        "area850" => Color.FromRgb(39, 208, 108),
        "war" => Color.FromRgb(180, 60, 20),
        _ => Color.FromRgb(138, 145, 153)
    };
}
