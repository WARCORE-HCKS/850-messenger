using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Area850.Services;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.Windows;

namespace Area850;

public partial class RoomChatWindow : Window
{
    private readonly HubClient _hub;
    private readonly string _slug;
    private readonly string? _password;
    private string _myRole = "member";
    private JsonElement _info;
    private readonly List<Occ> _people = new();
    private readonly Dictionary<string, RTCPeerConnection> _pcs = new(StringComparer.OrdinalIgnoreCase);
    private WindowsVideoEndPoint? _cam;
    private WindowsVideoEndPoint? _sink;
    private WindowsAudioEndPoint? _audio;
    private readonly DispatcherTimer _ptt = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _camOn;
    private bool _micHot;
    private bool _audioStarted;
    private bool _mousePtt;
    private string? _watching;

    public RoomChatWindow(HubClient hub, string slug, string title, string? password = null)
    {
        InitializeComponent();
        _hub = hub;
        _slug = slug;
        _password = password;
        TitleBlk.Text = title;
        Title = title + "  ·  room";
        EmojiBank.Fill(EmojiWrap, Composer, EmojiPop);
        _hub.Message += OnMsg;
        _hub.History += OnHist;
        _hub.SystemLine += t => Dispatcher.Invoke(() => Line(t, true));
        _hub.RoomRoster += OnRoster;
        _hub.RoomJoined += OnJoined;
        _hub.RoomLeft += OnLeft;
        _hub.RoomCam += OnCam;
        _hub.RoomMic += OnMic;
        _hub.RoomInfo += OnInfo;
        _hub.Kicked += OnKicked;
        _hub.Role += OnRole;
        _hub.Signal += OnSignal;
        _ptt.Tick += (_, _) =>
        {
            var down = _mousePtt || CtrlTalk();
            if (down != _micHot) _ = SetMic(down);
        };
        Loaded += async (_, _) =>
        {
            try
            {
                await _hub.JoinRoom(slug, password);
                _ptt.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "850");
                Close();
            }
        };
        Closed += async (_, _) =>
        {
            _ptt.Stop();
            _hub.Message -= OnMsg;
            _hub.History -= OnHist;
            _hub.RoomRoster -= OnRoster;
            _hub.RoomJoined -= OnJoined;
            _hub.RoomLeft -= OnLeft;
            _hub.RoomCam -= OnCam;
            _hub.RoomMic -= OnMic;
            _hub.RoomInfo -= OnInfo;
            _hub.Kicked -= OnKicked;
            _hub.Role -= OnRole;
            _hub.Signal -= OnSignal;
            await SetMic(false);
            await _hub.LeaveRoom(slug);
            StopCam();
            try { if (_audioStarted) await _audio!.CloseAudio(); } catch { }
            foreach (var pc in _pcs.Values) { try { pc.close(); } catch { } }
        };
    }

    private void OnMsg(JsonElement m) => Dispatcher.Invoke(() =>
    {
        if ((m.GetString("room") ?? "") != _slug) return;
        Line($"{m.GetString("display")}: {m.GetString("body")}", false);
    });

    private void OnHist(string room, JsonElement arr) => Dispatcher.Invoke(() =>
    {
        if (room != _slug) return;
        Bubbles.Children.Clear();
        foreach (var i in arr.EnumerateArray())
            Line($"{i.GetString("display")}: {i.GetString("body")}", i.GetString("kind") != "say");
    });

    private void OnRoster(string room, JsonElement members) => Dispatcher.Invoke(() =>
    {
        if (room != _slug) return;
        _people.Clear();
        foreach (var m in members.EnumerateArray())
            _people.Add(new Occ(
                m.GetString("username") ?? "",
                m.GetString("display") ?? "?",
                m.TryGetProperty("cam", out var c) && c.GetBoolean(),
                m.TryGetProperty("mic", out var mi) && mi.GetBoolean(),
                m.GetString("role") ?? "member"));
        PaintOcc();
    });

    private void OnJoined(string room, string user, string display, bool cam) => Dispatcher.Invoke(() =>
    {
        if (room != _slug) return;
        if (_people.All(p => !p.User.Equals(user, StringComparison.OrdinalIgnoreCase)))
            _people.Add(new Occ(user, display, cam, false, "member"));
        PaintOcc();
    });

    private void OnLeft(string room, string user) => Dispatcher.Invoke(() =>
    {
        if (room != _slug) return;
        _people.RemoveAll(p => p.User.Equals(user, StringComparison.OrdinalIgnoreCase));
        if (_watching != null && _watching.Equals(user, StringComparison.OrdinalIgnoreCase))
        {
            _watching = null;
            WatchLabel.Text = "No cam selected";
            WatchView.Source = null;
        }
        PaintOcc();
    });

    private void OnCam(string room, string user, string display, bool on) => Dispatcher.Invoke(() =>
    {
        if (room != _slug) return;
        var p = _people.FirstOrDefault(x => x.User.Equals(user, StringComparison.OrdinalIgnoreCase));
        if (p is not null) p.Cam = on;
        else _people.Add(new Occ(user, display, on, false, "member"));
        PaintOcc();
        if (on && _watching is null && !user.Equals(App.Username, StringComparison.OrdinalIgnoreCase))
            _ = Watch(user, display);
    });

    private void OnMic(string room, string user, string display, bool on) => Dispatcher.Invoke(() =>
    {
        if (room != _slug) return;
        var p = _people.FirstOrDefault(x => x.User.Equals(user, StringComparison.OrdinalIgnoreCase));
        if (p is not null) p.Mic = on;
        else _people.Add(new Occ(user, display, false, on, "member"));
        PaintOcc();
    });

    private void OnInfo(string room, JsonElement info) => Dispatcher.Invoke(() =>
    {
        if (room != _slug) return;
        _info = info;
        _myRole = info.GetString("myRole") ?? "member";
        var topic = info.GetString("topic");
        TopicBlk.Text = string.IsNullOrWhiteSpace(topic) ? "Hold Ctrl to talk." : topic;
        var owner = info.GetString("owner");
        if (!string.IsNullOrEmpty(owner))
            TitleBlk.Text = (info.GetString("title") ?? TitleBlk.Text) + "  ·  owner #" + owner;
        SettingsBtn.Visibility = Rank(_myRole) >= 3 ? Visibility.Visible : Visibility.Collapsed;
        PaintOcc();
    });

    private void OnKicked(string room, string why) => Dispatcher.Invoke(() =>
    {
        if (room != _slug) return;
        MessageBox.Show(why, "Bounced");
        Close();
    });

    private void OnRole(string room, string user, string role) => Dispatcher.Invoke(() =>
    {
        if (room != _slug) return;
        if (user.Equals(App.Username, StringComparison.OrdinalIgnoreCase)) _myRole = role;
        var p = _people.FirstOrDefault(x => x.User.Equals(user, StringComparison.OrdinalIgnoreCase));
        if (p is not null) p.Role = role;
        PaintOcc();
    });

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_info.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined)
        {
            if (_info.ValueKind != JsonValueKind.Object) return;
            new RoomSettingsWindow(_hub, _slug, _info) { Owner = this }.ShowDialog();
        }
    }

    private void PaintOcc()
    {
        Occupants.Items.Clear();
        foreach (var p in _people)
        {
            var row = new DockPanel { LastChildFill = true };
            var icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icons, Dock.Right);
            if (p.Cam) icons.Children.Add(LiveIcon("\uE714", Color.FromRgb(196, 43, 28), "Camera live"));
            if (p.Mic) icons.Children.Add(LiveIcon("\uE720", Color.FromRgb(39, 208, 108), "Mic hot"));
            row.Children.Add(icons);
            row.Children.Add(new TextBlock
            {
                Text = RoleMark(p.Role) + p.Display,
                FontWeight = p.Mic || p.Role is "owner" or "admin" ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            Occupants.Items.Add(new ListBoxItem
            {
                Content = row,
                Tag = p,
                ContextMenu = PersonMenu(p),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6, 8, 6)
            });
        }
    }

    private static TextBlock LiveIcon(string glyph, Color color, string tip) => new()
    {
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        Text = glyph,
        FontSize = 14,
        Foreground = new SolidColorBrush(color),
        Margin = new Thickness(6, 0, 0, 0),
        ToolTip = tip,
        VerticalAlignment = VerticalAlignment.Center
    };

    private ContextMenu PersonMenu(Occ p)
    {
        var m = new ContextMenu();
        if (!p.User.Equals(App.Username, StringComparison.OrdinalIgnoreCase))
        {
            if (p.Cam) Item(m, "Watch camera", async () => await Watch(p.User, p.Display));
            Item(m, "Send message", async () => await WhisperTo(p));
            Item(m, "Nudge", async () => { SoundBank.Nudge(); await _hub.Nudge(p.User); });
            Item(m, "Voice call", () => new CallWindow(p.User, p.Display, false, true, null, _hub).Show());
            Item(m, "Video call", () => new CallWindow(p.User, p.Display, true, true, null, _hub).Show());
            Item(m, "User details", async () => await ShowProfile(p.User));
            Item(m, "Add to contacts", async () =>
            {
                try { await _hub.AddFriend(p.User); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
            });
            Item(m, "Copy UIN", () => Clipboard.SetText(p.User));
            if (Rank(_myRole) > Rank(p.Role) && Rank(_myRole) >= 2)
            {
                m.Items.Add(new Separator());
                Item(m, "Bounce", async () => { try { await _hub.KickRoom(_slug, p.User, "Bounced."); } catch (Exception ex) { MessageBox.Show(ex.Message, "850"); } });
                Item(m, "Mute", async () => { try { await _hub.SetRoomRole(_slug, p.User, "muted"); } catch (Exception ex) { MessageBox.Show(ex.Message, "850"); } });
                Item(m, "Make member", async () => { try { await _hub.SetRoomRole(_slug, p.User, "member"); } catch (Exception ex) { MessageBox.Show(ex.Message, "850"); } });
                if (Rank(_myRole) >= 3)
                {
                    Item(m, "Make mod", async () => { try { await _hub.SetRoomRole(_slug, p.User, "mod"); } catch (Exception ex) { MessageBox.Show(ex.Message, "850"); } });
                    Item(m, "Make room admin", async () => { try { await _hub.SetRoomRole(_slug, p.User, "admin"); } catch (Exception ex) { MessageBox.Show(ex.Message, "850"); } });
                    Item(m, "Ban from room", async () =>
                    {
                        if (MessageBox.Show("Ban " + p.Display + " from this room?", "850", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                        try { await _hub.BanRoom(_slug, p.User); } catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
                    });
                }
            }
            m.Items.Add(new Separator());
            Item(m, "Block", async () =>
            {
                if (MessageBox.Show("Block " + p.Display + "?", "850", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                try { await _hub.Block(p.User); } catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
            });
        }
        else
        {
            Item(m, "This is you", () => { });
            Item(m, "Copy UIN", () => Clipboard.SetText(p.User));
        }
        return m;
    }

    private static void Item(ContextMenu m, string label, Action act)
    {
        var i = new MenuItem { Header = label };
        i.Click += (_, _) => act();
        m.Items.Add(i);
    }

    private async Task WhisperTo(Occ p)
    {
        var text = Ask("Message to " + p.Display, "Send");
        if (string.IsNullOrWhiteSpace(text)) return;
        text = EmojiBank.Expand(text.Trim());
        var dm = "dm:" + string.Join(":", new[] { App.Username, p.User }.OrderBy(x => x));
        try { await _hub.Send(dm, text); Line("(to " + p.Display + ") " + text, true); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
    }

    private async Task ShowProfile(string uin)
    {
        try
        {
            var profile = await _hub.GetProfile(uin);
            new ProfileWindow(_hub, profile, uin.Equals(App.Username, StringComparison.OrdinalIgnoreCase)) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "850"); }
    }

    private void Occupants_Right(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem li)
            {
                Occupants.SelectedItem = li;
                break;
            }
        }
    }

    private void Emoji_Click(object sender, RoutedEventArgs e) => EmojiPop.IsOpen = !EmojiPop.IsOpen;

    private async void Watch_Sel(object sender, SelectionChangedEventArgs e)
    {
        if (Occupants.SelectedItem is not ListBoxItem { Tag: Occ p }) return;
        if (p.User.Equals(App.Username, StringComparison.OrdinalIgnoreCase)) return;
        if (!p.Cam)
        {
            WatchLabel.Text = p.Display + " has no camera on";
            return;
        }
        await Watch(p.User, p.Display);
    }

    private async Task Watch(string user, string display)
    {
        _watching = user;
        WatchLabel.Text = "Watching " + display;
        await _hub.SignalTo(user, "room-watch", _slug);
    }

    private async void Cam_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_camOn)
            {
                StopCam();
                await _hub.SetRoomCam(_slug, false);
                CamBtn.Content = "Start camera";
                return;
            }
            _cam = new WindowsVideoEndPoint(new VpxVideoEncoder());
            _cam.RestrictFormats(f => f.Codec == VideoCodecsEnum.VP8);
            var fmts = _cam.GetVideoSourceFormats();
            if (fmts.Count == 0) throw new InvalidOperationException("No camera format available.");
            _cam.SetVideoSourceFormat(fmts[0]);
            _cam.OnVideoSourceEncodedSample += (dur, sample) =>
            {
                foreach (var pc in _pcs.Values)
                    try { pc.SendVideo(dur, sample); } catch { }
            };
            _cam.OnVideoSourceRawSample += OnLocalRaw;
            await _cam.InitialiseVideoSourceDevice();
            await _cam.StartVideo();
            _camOn = true;
            CamBtn.Content = "Stop camera";
            LocalBox.Visibility = Visibility.Visible;
            if (_watching is null)
            {
                WatchLabel.Visibility = Visibility.Collapsed;
                WatchLabel.Text = "You · live";
            }
            await _hub.SetRoomCam(_slug, true);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Camera: " + ex.Message, "850");
        }
    }

    private void StopCam()
    {
        _camOn = false;
        try { if (_cam is not null) _cam.OnVideoSourceRawSample -= OnLocalRaw; } catch { }
        try { _ = _cam?.CloseVideo(); } catch { }
        _cam = null;
        Dispatcher.Invoke(() =>
        {
            LocalBox.Visibility = Visibility.Collapsed;
            LocalView.Source = null;
            if (_watching is null)
            {
                WatchView.Source = null;
                WatchLabel.Visibility = Visibility.Visible;
                WatchLabel.Text = "Start camera to see yourself";
            }
        });
    }

    private void OnLocalRaw(uint timestamp, int width, int height, byte[] sample, VideoPixelFormatsEnum fmt)
    {
        Dispatcher.BeginInvoke(() =>
        {
            VideoPaint.ToImage(LocalView, sample, width, height, fmt);
            if (_watching is null)
            {
                VideoPaint.ToImage(WatchView, sample, width, height, fmt);
                WatchLabel.Visibility = Visibility.Collapsed;
            }
        });
    }

    private async void OnSignal(string user, string display, string kind, string payload)
    {
        if (!kind.StartsWith("room-")) return;
        var data = payload;
        var nl = payload.IndexOf('\n');
        if (nl >= 0)
        {
            if (payload[..nl] != _slug) return;
            data = payload[(nl + 1)..];
        }
        await Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (kind == "room-talk")
                {
                    await EnsurePc(user, false);
                }
                else if (kind == "room-watch")
                {
                    if (!_camOn) return;
                    var pc = await EnsurePc(user, true);
                    var offer = pc.createOffer();
                    await pc.setLocalDescription(offer);
                    await _hub.SignalTo(user, "room-offer", _slug + "\n" + offer.sdp);
                }
                else if (kind == "room-offer")
                {
                    var pc = await EnsurePc(user, false);
                    pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = data });
                    var ans = pc.createAnswer();
                    await pc.setLocalDescription(ans);
                    await _hub.SignalTo(user, "room-answer", _slug + "\n" + ans.sdp);
                    _watching = user;
                    WatchLabel.Text = "Watching " + display;
                }
                else if (kind == "room-answer" && _pcs.TryGetValue(user, out var pca))
                    pca.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = data });
                else if (kind == "room-ice" && _pcs.TryGetValue(user, out var pci))
                    pci.addIceCandidate(new RTCIceCandidateInit { candidate = data, sdpMid = "0" });
            }
            catch (Exception ex) { WatchLabel.Text = ex.Message; }
        });
    }

    private async Task SetMic(bool on)
    {
        if (on == _micHot) return;
        if (on && App.Invisible)
        {
            Line("Turn off invisible before talking.", true);
            return;
        }
        _micHot = on;
        var me = _people.FirstOrDefault(x => x.User.Equals(App.Username, StringComparison.OrdinalIgnoreCase));
        if (me is not null) me.Mic = on;
        Dispatcher.Invoke(() =>
        {
            TalkBtn.Background = new SolidColorBrush(on ? Color.FromRgb(196, 43, 28) : Color.FromRgb(39, 208, 108));
            TalkLabel.Text = on ? "MIC HOT  ·  let go" : "Hold Ctrl to talk";
            PaintOcc();
        });
        try { await _hub.SetRoomMic(_slug, on); } catch { }
        if (!on) return;
        try
        {
            await HookAudio();
            foreach (var p in _people)
            {
                if (p.User.Equals(App.Username, StringComparison.OrdinalIgnoreCase)) continue;
                _ = ConnectTalk(p.User);
            }
        }
        catch (Exception ex) { Line("Mic: " + ex.Message, true); _micHot = false; }
    }

    private async Task ConnectTalk(string user)
    {
        try
        {
            await _hub.SignalTo(user, "room-talk", _slug);
            var pc = await EnsurePc(user, true);
            if (pc.signalingState == RTCSignalingState.stable && pc.remoteDescription == null)
            {
                var offer = pc.createOffer();
                await pc.setLocalDescription(offer);
                await _hub.SignalTo(user, "room-offer", _slug + "\n" + offer.sdp);
            }
        }
        catch { }
    }

    private async Task HookAudio()
    {
        if (_audio is not null)
        {
            if (!_audioStarted) { await _audio.StartAudio(); _audioStarted = true; }
            return;
        }
        _audio = new WindowsAudioEndPoint(new AudioEncoder());
        _audio.OnAudioSourceEncodedSample += (dur, sample) =>
        {
            if (!_micHot) return;
            foreach (var pc in _pcs.Values)
                try { pc.SendAudio(dur, sample); } catch { }
        };
        await _audio.StartAudio();
        _audioStarted = true;
    }

    private async Task<RTCPeerConnection> EnsurePc(string user, bool sending)
    {
        if (_pcs.TryGetValue(user, out var existing)) return existing;
        var pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }]
        });
        await HookAudio();
        if (_audio is not null)
        {
            pc.addTrack(new MediaStreamTrack(_audio.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv));
            pc.OnAudioFormatsNegotiated += fmts => { try { _audio.SetAudioSourceFormat(fmts.First()); } catch { } };
            pc.OnRtpPacketReceived += (ep, media, pkt) =>
            {
                if (media == SDPMediaTypesEnum.audio && _audio is not null)
                    _audio.GotAudioRtp(ep, pkt.Header.SyncSource, pkt.Header.SequenceNumber, pkt.Header.Timestamp, pkt.Header.PayloadType, pkt.Header.MarkerBit != 0, pkt.Payload);
            };
        }
        if (_cam is not null)
        {
            pc.addTrack(new MediaStreamTrack(_cam.GetVideoSourceFormats(), MediaStreamStatusEnum.SendRecv));
            pc.OnVideoFormatsNegotiated += fmts => _cam.SetVideoSourceFormat(fmts.First());
        }
        _sink ??= new WindowsVideoEndPoint(new VpxVideoEncoder());
        _sink.OnVideoSinkDecodedSample += (sample, w, h, stride, fmt) => ShowFrame(sample, w, h, stride);
        if (_cam is null)
            pc.addTrack(new MediaStreamTrack(_sink.GetVideoSinkFormats(), MediaStreamStatusEnum.RecvOnly));
        pc.OnVideoFormatsNegotiated += fmts => { try { _sink.SetVideoSinkFormat(fmts.First()); } catch { } };
        pc.OnVideoFrameReceived += _sink.GotVideoFrame;
        pc.onicecandidate += cand =>
        {
            if (cand is not null) _ = _hub.SignalTo(user, "room-ice", _slug + "\n" + cand.candidate);
        };
        _pcs[user] = pc;
        return pc;
    }

    private void ShowFrame(byte[] sample, uint width, uint height, int stride)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                VideoPaint.ToImage(WatchView, sample, (int)width, (int)height, VideoPixelFormatsEnum.Bgr);
                WatchLabel.Visibility = Visibility.Collapsed;
            }
            catch { }
        });
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await Send();
    private async void Composer_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await Send(); }
    }

    private async Task Send()
    {
        var t = EmojiBank.Expand(Composer.Text.Trim());
        if (t.Length == 0) return;
        Composer.Text = "";
        await _hub.Send(_slug, t);
    }

    private void Line(string text, bool sys)
    {
        var tb = EmojiBank.Message(text, sys ? (Brush)FindResource("Sub") : (Brush)FindResource("Ink"), sys ? 12 : 13.5);
        tb.Margin = new Thickness(0, 0, 0, 6);
        tb.FontStyle = sys ? FontStyles.Italic : FontStyles.Normal;
        Bubbles.Children.Add(tb);
        ChatScroll.ScrollToEnd();
    }

    private string? Ask(string label, string title)
    {
        var tb = new TextBox { Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "Send", Height = 34, IsDefault = true, Background = (Brush)FindResource("Accent"), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(tb);
        panel.Children.Add(ok);
        var w = new Window
        {
            Title = title,
            Width = 320,
            Height = 160,
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

    private static string RoleMark(string role) => role switch
    {
        "owner" => "👑 ",
        "admin" => "⚡ ",
        "mod" => "🛡 ",
        "muted" => "🔇 ",
        _ => ""
    };

    private static int Rank(string role) => role switch
    {
        "owner" => 4,
        "admin" => 3,
        "mod" => 2,
        "muted" => 0,
        "banned" => -1,
        _ => 1
    };

    private sealed class Occ
    {
        public string User, Display, Role;
        public bool Cam;
        public bool Mic;
        public Occ(string u, string d, bool c, bool m = false, string role = "member")
        {
            User = u; Display = d; Cam = c; Mic = m; Role = role;
        }
    }

    private void Talk_Down(object sender, MouseButtonEventArgs e)
    {
        _mousePtt = true;
        TalkBtn.CaptureMouse();
        e.Handled = true;
        _ = SetMic(true);
    }

    private void Talk_Up(object sender, MouseButtonEventArgs e)
    {
        _mousePtt = false;
        if (TalkBtn.IsMouseCaptured) TalkBtn.ReleaseMouseCapture();
        e.Handled = true;
        if (!CtrlTalk()) _ = SetMic(false);
    }

    private static bool CtrlTalk()
    {
        if ((GetAsyncKeyState(0x11) & 0x8000) == 0) return false;
        foreach (var vk in new[] { 0x41, 0x43, 0x46, 0x53, 0x56, 0x58, 0x5A })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return false;
        return true;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
