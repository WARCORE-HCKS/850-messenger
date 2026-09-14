using System.Windows;
using System.Windows.Controls;
using Area850.Services;

namespace Area850;

public partial class CreateRoomWindow : Window
{
    private readonly HubClient _hub;
    public string? CreatedSlug { get; private set; }
    public string? CreatedTitle { get; private set; }

    public static readonly (string id, string label)[] Channels =
    [
        ("hangout", "Hangout"),
        ("music", "Music"),
        ("tech", "Tech"),
        ("gaming", "Gaming"),
        ("dating", "Dating"),
        ("afterdark", "After Dark"),
        ("area850", "Area 850"),
        ("war", "War Room")
    ];

    public CreateRoomWindow(HubClient hub)
    {
        InitializeComponent();
        _hub = hub;
        foreach (var (id, label) in Channels)
            ChanBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        ChanBox.SelectedIndex = 0;
    }

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        var title = NameBox.Text.Trim();
        var ch = (ChanBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "hangout";
        try
        {
            var r = await _hub.CreateRoom(title, ch, PassBox.Password, TopicBox.Text, WelcomeBox.Text);
            CreatedSlug = r.GetString("slug");
            CreatedTitle = r.GetString("title") ?? title;
            DialogResult = true;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
    }
}
