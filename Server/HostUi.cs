using System.Text.Json;
using Area850.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Area850.Server;

internal sealed class ServerForm : Form
{
    private readonly Action _stop;
    private readonly AdminOps _ops;
    private readonly IHubContext<ChatHub> _hub;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _tick;
    private readonly Label _live;
    private Label _online = null!;
    private Label _rooms = null!;
    private Label _sitting = null!;
    private Label _users = null!;
    private Label _today = null!;
    private DataGridView _sessions = null!;
    private DataGridView _userList = null!;
    private DataGridView _roomList = null!;
    private DataGridView _msgList = null!;
    private TextBox _userQ = null!;
    private TextBox _msgQ = null!;
    private readonly List<Button> _tabBtns = new();
    private readonly List<Panel> _pages = new();
    private bool _exit;

    public ServerForm(string listen, string lan, Action stop, AdminOps ops, IHubContext<ChatHub> hub)
    {
        _stop = stop;
        _ops = ops;
        _hub = hub;
        Text = "850 Server";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 620);
        BackColor = Color.FromArgb(14, 17, 20);
        ForeColor = Color.FromArgb(232, 236, 240);
        Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? SystemIcons.Application; } catch { }

        var head = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Color.FromArgb(23, 27, 32) };
        var title = new Label { Text = "850 Server", Font = new Font("Segoe UI", 16, FontStyle.Bold), AutoSize = true, Location = new Point(16, 10), ForeColor = ForeColor };
        _live = new Label { Text = "●  LIVE   " + listen + "   LAN " + lan, AutoSize = true, Location = new Point(16, 42), ForeColor = Color.FromArgb(39, 208, 108) };
        var hide = DarkBtn("Hide to tray", 0, 0, 110, 32);
        hide.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        hide.Location = new Point(ClientSize.Width - 250, 20);
        hide.Click += (_, _) => Hide();
        var stopBtn = DarkBtn("Stop", 0, 0, 90, 32);
        stopBtn.BackColor = Color.FromArgb(196, 43, 28);
        stopBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        stopBtn.Location = new Point(ClientSize.Width - 120, 20);
        stopBtn.Click += (_, _) => Quit();
        head.Controls.Add(title);
        head.Controls.Add(_live);
        head.Controls.Add(hide);
        head.Controls.Add(stopBtn);
        head.Resize += (_, _) =>
        {
            hide.Location = new Point(head.Width - 250, 20);
            stopBtn.Location = new Point(head.Width - 120, 20);
        };

        var strip = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Pal.Bg,
            Padding = new Padding(12, 8, 12, 0),
            WrapContents = false
        };
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Pal.Bg, Padding = new Padding(8, 4, 8, 8) };
        var names = new[] { "Overview", "Users", "Rooms", "Traffic" };
        var pages = new[] { MakeOverview(), MakeUsers(), MakeRooms(), MakeTraffic() };
        for (var i = 0; i < names.Length; i++)
        {
            var idx = i;
            var btn = new Button
            {
                Text = names[i],
                Width = 108,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = Pal.Card,
                ForeColor = Pal.Sub,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Margin = new Padding(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Pal.GreenDim;
            btn.Click += (_, _) => ShowTab(idx);
            strip.Controls.Add(btn);
            _tabBtns.Add(btn);
            pages[i].Dock = DockStyle.Fill;
            pages[i].Visible = false;
            _pages.Add(pages[i]);
            body.Controls.Add(pages[i]);
        }
        ShowTab(0);

        Controls.Add(body);
        Controls.Add(strip);
        Controls.Add(head);

        _tray = new NotifyIcon { Text = "850 Server · live", Visible = true, Icon = Icon };
        _tray.DoubleClick += (_, _) => Reveal();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Reveal());
        menu.Items.Add("Stop server", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        try { _tray.ShowBalloonTip(2200, "850 Server", "Live — admin is in this window.", ToolTipIcon.Info); } catch { }

        _tick = new System.Windows.Forms.Timer { Interval = 2000 };
        _tick.Tick += (_, _) => RefreshOverview();
        _tick.Start();
        Shown += (_, _) => { RefreshOverview(); LoadUsers(); LoadRooms(); LoadMsgs(); };

        FormClosing += (_, e) =>
        {
            if (_exit) return;
            e.Cancel = true;
            Hide();
        };
        FormClosed += (_, _) =>
        {
            _tick.Stop();
            _tray.Visible = false;
            _tray.Dispose();
        };
    }

    public void Reveal()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ShowTab(int i)
    {
        for (var n = 0; n < _pages.Count; n++)
        {
            _pages[n].Visible = n == i;
            _pages[n].BringToFront();
            _tabBtns[n].BackColor = n == i ? Pal.GreenDim : Pal.Card;
            _tabBtns[n].ForeColor = n == i ? Pal.Green : Pal.Sub;
        }
        if (i == 0) RefreshOverview();
        if (i == 1) LoadUsers();
        if (i == 2) LoadRooms();
        if (i == 3) LoadMsgs();
    }

    private Panel Page() => new() { BackColor = Pal.Bg, ForeColor = Pal.Ink };

    private Panel MakeOverview()
    {
        var page = Page();
        var cards = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(12, 12, 12, 0), BackColor = Pal.Bg };
        _online = Card(cards, "ONLINE");
        _sitting = Card(cards, "IN ROOMS");
        _rooms = Card(cards, "LIVE ROOMS");
        _users = Card(cards, "ACCOUNTS");
        _today = Card(cards, "MSGS TODAY");
        var sessions = Grid("UIN", "Name", "Status");
        sessions.Dock = DockStyle.Fill;
        _sessions = sessions;
        var kickBar = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Pal.Bg, Padding = new Padding(12, 8, 12, 8) };
        var kick = DarkBtn("Kick selected", 12, 8, 140, 32);
        kick.Click += async (_, _) =>
        {
            var u = Sel(_sessions);
            if (u is null) return;
            await Act(() => _ops.Kick(u.TrimStart('#'), "Signed out by admin.", Notify));
            RefreshOverview();
        };
        kickBar.Controls.Add(kick);
        page.Controls.Add(sessions);
        page.Controls.Add(kickBar);
        page.Controls.Add(cards);
        return page;
    }

    private Panel MakeUsers()
    {
        var page = Page();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 10, 12, 0), BackColor = Pal.Bg };
        var q = DarkBox(220, "Search UIN or name");
        _userQ = q;
        var go = DarkBtn("Search", 0, 0, 80, 28);
        go.Click += (_, _) => LoadUsers();
        q.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LoadUsers(); } };
        bar.Controls.Add(q);
        bar.Controls.Add(go);
        var list = Grid("UIN", "Name", "Flags");
        _userList = list;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 8, 12, 8), BackColor = Pal.Bg };
        actions.Controls.Add(ActBtn("Password", async () =>
        {
            var u = Sel(list); if (u is null) return;
            var pw = Prompt("New password for #" + u);
            if (string.IsNullOrEmpty(pw)) return;
            await Act(() => { _ops.SetPassword(u, pw); return Task.CompletedTask; });
        }));
        actions.Controls.Add(ActBtn("Kick", async () =>
        {
            var u = Sel(list); if (u is null) return;
            await Act(() => _ops.Kick(u, "Signed out by admin.", Notify));
            LoadUsers(); RefreshOverview();
        }));
        actions.Controls.Add(ActBtn("Disable", async () =>
        {
            var u = Sel(list); if (u is null) return;
            await Act(() => _ops.Disable(u, true, "0", Notify));
            LoadUsers();
        }));
        actions.Controls.Add(ActBtn("Enable", async () =>
        {
            var u = Sel(list); if (u is null) return;
            await Act(() => _ops.Disable(u, false, "0", Notify));
            LoadUsers();
        }));
        var del = ActBtn("Delete", async () =>
        {
            var u = Sel(list); if (u is null) return;
            if (MessageBox.Show("Delete #" + u + " forever?", "850 Server", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await Act(() => _ops.DeleteUser(u, Notify));
            LoadUsers();
        });
        del.BackColor = Color.FromArgb(196, 43, 28);
        actions.Controls.Add(del);
        page.Controls.Add(list);
        page.Controls.Add(actions);
        page.Controls.Add(bar);
        return page;
    }

    private Panel MakeRooms()
    {
        var page = Page();
        var list = Grid("Room", "Channel", "In", "Msgs", "Flags");
        _roomList = list;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 8, 12, 8), BackColor = Pal.Bg };
        actions.Controls.Add(ActBtn("Announce", async () =>
        {
            var slug = Sel(list); if (slug is null) return;
            var body = Prompt("Message to /" + slug);
            if (string.IsNullOrWhiteSpace(body)) return;
            await _hub.Clients.Group("room:" + slug).SendAsync("System", slug, "ADMIN: " + body);
        }));
        actions.Controls.Add(ActBtn("Lock door", async () =>
        {
            var slug = Sel(list); if (slug is null) return;
            await Act(() => { _ops.LockRoom(slug, true); return Task.CompletedTask; });
            LoadRooms();
        }));
        actions.Controls.Add(ActBtn("Unlock", async () =>
        {
            var slug = Sel(list); if (slug is null) return;
            await Act(() => { _ops.LockRoom(slug, false); return Task.CompletedTask; });
            LoadRooms();
        }));
        actions.Controls.Add(ActBtn("Clear password", async () =>
        {
            var slug = Sel(list); if (slug is null) return;
            await Act(() => { _ops.ClearPass(slug); return Task.CompletedTask; });
            LoadRooms();
        }));
        var del = ActBtn("Delete room", async () =>
        {
            var slug = Sel(list); if (slug is null) return;
            if (MessageBox.Show("Delete room " + slug + "?", "850 Server", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await _hub.Clients.Group("room:" + slug).SendAsync("Kicked", slug, "Room closed by admin.");
            await Act(() => { _ops.DeleteRoom(slug); return Task.CompletedTask; });
            LoadRooms();
        });
        del.BackColor = Color.FromArgb(196, 43, 28);
        actions.Controls.Add(del);
        page.Controls.Add(list);
        page.Controls.Add(actions);
        return page;
    }

    private Panel MakeTraffic()
    {
        var page = Page();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 10, 12, 0), BackColor = Pal.Bg };
        var q = DarkBox(220, "Room slug (blank = all)");
        _msgQ = q;
        var go = DarkBtn("Load", 0, 0, 80, 28);
        go.Click += (_, _) => LoadMsgs();
        bar.Controls.Add(q);
        bar.Controls.Add(go);
        var list = Grid("When", "Room", "Who", "Message");
        _msgList = list;
        page.Controls.Add(list);
        page.Controls.Add(bar);
        return page;
    }

    private void RefreshOverview()
    {
        try
        {
            var (online, rooms, sitting) = ChatHub.AdminCounts();
            _online.Text = online.ToString();
            _rooms.Text = rooms.ToString();
            _sitting.Text = sitting.ToString();
            _live.ForeColor = Color.FromArgb(39, 208, 108);
            _tray.Text = $"850 Server · {online} online";
            var ov = _ops.Overview();
            var json = JsonSerializer.Serialize(ov);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _users.Text = root.GetProperty("users").GetInt64().ToString();
            _today.Text = root.GetProperty("today").GetInt64().ToString();
            _sessions.Rows.Clear();
            if (root.GetProperty("live").TryGetProperty("sessions", out var sess))
                foreach (var p in sess.EnumerateArray())
                {
                    var uin = p.GetProperty("username").GetString() ?? "";
                    var ghost = p.TryGetProperty("invisible", out var g) && g.GetBoolean();
                    AddRow(_sessions, uin, "#" + uin, p.GetProperty("display").GetString() ?? "", ghost ? "ghost" : (p.GetProperty("status").GetString() ?? ""));
                }
        }
        catch
        {
            _live.ForeColor = Color.FromArgb(240, 176, 32);
        }
    }

    private void LoadUsers()
    {
        _userList.Rows.Clear();
        foreach (var o in _ops.Users(_userQ.Text))
        {
            var json = JsonSerializer.Serialize(o);
            using var doc = JsonDocument.Parse(json);
            var u = doc.RootElement;
            var flags = (u.GetProperty("isAdmin").GetBoolean() ? "admin " : "") + (u.GetProperty("disabled").GetBoolean() ? "disabled" : "");
            AddRow(_userList, u.GetProperty("username").GetString(), "#" + u.GetProperty("uin").GetString(), u.GetProperty("display").GetString() ?? "", flags.Trim());
        }
    }

    private void LoadRooms()
    {
        _roomList.Rows.Clear();
        foreach (var o in _ops.Rooms())
        {
            var json = JsonSerializer.Serialize(o);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var flags = (r.GetProperty("locked").GetBoolean() ? "password " : "") + (r.GetProperty("doorLock").GetBoolean() ? "locked " : "") + (r.GetProperty("slow").GetBoolean() ? "slow" : "");
            AddRow(_roomList, r.GetProperty("slug").GetString(),
                r.GetProperty("title").GetString() ?? "",
                r.GetProperty("channel").GetString() ?? "",
                r.GetProperty("users").GetInt32().ToString(),
                r.GetProperty("messages").GetInt64().ToString(),
                flags.Trim());
        }
    }

    private void LoadMsgs()
    {
        _msgList.Rows.Clear();
        foreach (var o in _ops.Messages(_msgQ.Text, 120))
        {
            var json = JsonSerializer.Serialize(o);
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            var at = (m.GetProperty("at").GetString() ?? "").Replace("T", " ");
            if (at.Length > 19) at = at[..19];
            AddRow(_msgList, null, at, m.GetProperty("room").GetString() ?? "", m.GetProperty("display").GetString() ?? "", m.GetProperty("body").GetString() ?? "");
        }
    }

    private Task Notify(string conn, string why) =>
        _hub.Clients.Client(conn).SendAsync("Kicked", "", why);

    private static string? Sel(DataGridView grid)
    {
        if (grid.CurrentRow is null) return null;
        return (grid.CurrentRow.Tag as string) ?? grid.CurrentRow.Cells[0].Value?.ToString()?.TrimStart('#');
    }

    private static void AddRow(DataGridView grid, string? tag, params string?[] cells)
    {
        var i = grid.Rows.Add(cells.Select(c => (object?)c ?? "").ToArray());
        grid.Rows[i].Tag = tag;
    }

    private async Task Act(Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850 Server"); }
    }

    private Button ActBtn(string text, Func<Task> click)
    {
        var b = DarkBtn(text, 0, 0, 120, 28);
        b.Click += async (_, _) => await click();
        return b;
    }

    private static Label Card(Control parent, string caption)
    {
        var box = new Panel { Width = 130, Height = 70, BackColor = Pal.Card, Margin = new Padding(0, 0, 8, 0) };
        var n = new Label { Text = "0", Font = new Font("Segoe UI", 18, FontStyle.Bold), ForeColor = Pal.Green, Location = new Point(10, 8), AutoSize = true };
        var c = new Label { Text = caption, ForeColor = Pal.Sub, Location = new Point(10, 42), AutoSize = true, Font = new Font("Segoe UI", 8) };
        box.Controls.Add(n);
        box.Controls.Add(c);
        parent.Controls.Add(box);
        return n;
    }

    private static TextBox DarkBox(int width, string placeholder) => new()
    {
        Width = width,
        Height = 28,
        PlaceholderText = placeholder,
        BackColor = Pal.Card,
        ForeColor = Pal.Ink,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 10f)
    };

    private DataGridView Grid(params string[] cols)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Pal.Card,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.None,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            EnableHeadersVisualStyles = false,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 32,
            GridColor = Pal.Line
        };
        g.RowTemplate.Height = 30;
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Pal.Card,
            ForeColor = Pal.Sub,
            SelectionBackColor = Pal.Card,
            SelectionForeColor = Pal.Sub,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Padding = new Padding(8, 0, 0, 0)
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Pal.Card,
            ForeColor = Pal.Ink,
            SelectionBackColor = Pal.GreenDim,
            SelectionForeColor = Pal.Green,
            Padding = new Padding(8, 0, 0, 0)
        };
        g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(20, 24, 28),
            ForeColor = Pal.Ink,
            SelectionBackColor = Pal.GreenDim,
            SelectionForeColor = Pal.Green,
            Padding = new Padding(8, 0, 0, 0)
        };
        foreach (var c in cols)
            g.Columns.Add(c, c);
        return g;
    }

    private Button DarkBtn(string text, int x, int y, int w, int h)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            FlatStyle = FlatStyle.Flat,
            BackColor = Pal.Green,
            ForeColor = Pal.OnGreen,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    private static string? Prompt(string label)
    {
        using var f = new Form
        {
            Text = "850 Server",
            Width = 340,
            Height = 150,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Pal.Card,
            ForeColor = Pal.Ink
        };
        var tb = new TextBox { Left = 16, Top = 40, Width = 290, BackColor = Pal.Bg, ForeColor = Pal.Ink, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "OK", Left = 230, Top = 75, Width = 76, DialogResult = DialogResult.OK };
        f.Controls.Add(new Label { Text = label, Left = 16, Top = 12, AutoSize = true, ForeColor = Color.White });
        f.Controls.Add(tb);
        f.Controls.Add(ok);
        f.AcceptButton = ok;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }

    private void Quit()
    {
        _exit = true;
        _tray.Visible = false;
        Hide();
        _stop();
        Close();
    }
}

internal static class Pal
{
    public static readonly Color Bg = Color.FromArgb(14, 17, 20);
    public static readonly Color Card = Color.FromArgb(23, 27, 32);
    public static readonly Color Ink = Color.FromArgb(232, 236, 240);
    public static readonly Color Sub = Color.FromArgb(138, 145, 153);
    public static readonly Color Line = Color.FromArgb(42, 48, 56);
    public static readonly Color Green = Color.FromArgb(39, 208, 108);
    public static readonly Color GreenDim = Color.FromArgb(28, 58, 40);
    public static readonly Color OnGreen = Color.FromArgb(6, 40, 20);
}


