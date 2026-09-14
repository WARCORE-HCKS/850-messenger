using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Area850.Services;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.Windows;

namespace Area850;

public partial class CallWindow : Window
{
    private readonly string _peer;
    private readonly bool _video;
    private readonly bool _out;
    private readonly HubClient _hub;
    private RTCPeerConnection? _pc;
    private WindowsAudioEndPoint? _audio;
    private WindowsVideoEndPoint? _cam;

    public CallWindow(string peerUser, string peerDisplay, bool video, bool outgoing, string? offer, HubClient hub)
    {
        InitializeComponent();
        _peer = peerUser;
        _video = video;
        _out = outgoing;
        _hub = hub;
        Who.Text = peerDisplay;
        State.Text = outgoing ? (video ? "Video calling…" : "Calling…") : "Connecting…";
        Loaded += async (_, _) => await Start(offer);
        Closed += (_, _) => TearDown();
        _hub.Signal += OnSignal;
    }

    private void OnSignal(string user, string display, string kind, string payload)
    {
        if (!user.Equals(_peer, StringComparison.OrdinalIgnoreCase) || _pc is null) return;
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (kind == "answer")
                    _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = payload });
                else if (kind == "ice")
                    _pc.addIceCandidate(new RTCIceCandidateInit { candidate = payload, sdpMid = "0" });
            }
            catch (Exception ex) { State.Text = ex.Message; }
            await Task.CompletedTask;
        });
    }

    private async Task Start(string? offer)
    {
        try
        {
            _pc = new RTCPeerConnection(new RTCConfiguration
            {
                iceServers = [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }]
            });

            _audio = new WindowsAudioEndPoint(new AudioEncoder());
            _pc.addTrack(new MediaStreamTrack(_audio.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv));
            _audio.OnAudioSourceEncodedSample += _pc.SendAudio;
            _pc.OnAudioFormatsNegotiated += fmts => _audio.SetAudioSourceFormat(fmts.First());
            _pc.OnRtpPacketReceived += (ep, media, pkt) =>
            {
                if (media == SDPMediaTypesEnum.audio)
                    _audio.GotAudioRtp(ep, pkt.Header.SyncSource, pkt.Header.SequenceNumber, pkt.Header.Timestamp, pkt.Header.PayloadType, pkt.Header.MarkerBit != 0, pkt.Payload);
            };

            if (_video)
            {
                _cam = new WindowsVideoEndPoint(new VpxVideoEncoder());
                _cam.RestrictFormats(f => f.Codec == VideoCodecsEnum.VP8);
                var vfmts = _cam.GetVideoSourceFormats();
                if (vfmts.Count > 0) _cam.SetVideoSourceFormat(vfmts[0]);
                _pc.addTrack(new MediaStreamTrack(_cam.GetVideoSourceFormats(), MediaStreamStatusEnum.SendRecv));
                _cam.OnVideoSourceEncodedSample += _pc.SendVideo;
                _pc.OnVideoFormatsNegotiated += fmts => _cam.SetVideoSourceFormat(fmts.First());
                _cam.OnVideoSinkDecodedSample += OnSample;
                _cam.OnVideoSourceRawSample += (_, w, h, sample, fmt) =>
                    Dispatcher.BeginInvoke(() => VideoPaint.ToImage(Local, sample, w, h, fmt));
                _pc.OnVideoFrameReceived += _cam.GotVideoFrame;
                await _cam.InitialiseVideoSourceDevice();
                await _cam.StartVideo();
            }
            await _audio.StartAudio();

            _pc.oniceconnectionstatechange += st => Dispatcher.Invoke(() =>
            {
                if (st == RTCIceConnectionState.connected) State.Text = "Connected · full duplex";
            });
            _pc.onicecandidate += cand =>
            {
                if (cand is not null) _ = _hub.SignalTo(_peer, "ice", cand.candidate);
            };

            if (_out)
            {
                var offerDesc = _pc.createOffer();
                await _pc.setLocalDescription(offerDesc);
                await _hub.SignalTo(_peer, _video ? "offer-video" : "offer-audio", offerDesc.sdp);
            }
            else if (!string.IsNullOrEmpty(offer))
            {
                _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer });
                var ans = _pc.createAnswer();
                await _pc.setLocalDescription(ans);
                await _hub.SignalTo(_peer, "answer", ans.sdp);
            }
        }
        catch (Exception ex)
        {
            State.Text = "Call failed: " + ex.Message;
        }
    }

    private void OnSample(byte[] sample, uint width, uint height, int stride, VideoPixelFormatsEnum fmt)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var bmp = new WriteableBitmap((int)width, (int)height, 96, 96, PixelFormats.Bgr24, null);
                bmp.WritePixels(new Int32Rect(0, 0, (int)width, (int)height), sample, stride, 0);
                Remote.Source = bmp;
            }
            catch { }
        });
    }

    private void Hang_Click(object sender, RoutedEventArgs e) => Close();

    private void TearDown()
    {
        try { _pc?.close(); } catch { }
        try { _ = _audio?.CloseAudio(); } catch { }
        try { _ = _cam?.CloseVideo(); } catch { }
    }
}
