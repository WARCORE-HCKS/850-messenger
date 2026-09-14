using Microsoft.AspNetCore.SignalR;

namespace Area850.Net;

public sealed class NetHub : Hub
{
    private readonly Roster _roster;
    public NetHub(Roster roster) => _roster = roster;

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var left = _roster.Remove(Context.ConnectionId);
        if (left is not null)
            await Clients.Group(left.Net).SendAsync("Left", left.Id, left.Callsign);
        await base.OnDisconnectedAsync(exception);
    }

    public object Hello(string callsign)
    {
        return new
        {
            id = Context.ConnectionId,
            nets = Roster.Nets,
            census = _roster.Census(),
            suggested = Roster.Sanitize(callsign)
        };
    }

    public async Task<object> Join(string callsign, string net)
    {
        if (!Roster.Nets.Contains(net)) net = "Lobby";
        var existing = _roster.Get(Context.ConnectionId);
        if (existing is not null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, existing.Net);
            await Clients.Group(existing.Net).SendAsync("Left", existing.Id, existing.Callsign);
            _roster.Remove(Context.ConnectionId);
        }

        var me = _roster.Add(Context.ConnectionId, callsign, net);
        await Groups.AddToGroupAsync(Context.ConnectionId, me.Net);
        var others = _roster.InNet(me.Net).Where(m => m.Id != me.Id).ToList();
        await Clients.GroupExcept(me.Net, Context.ConnectionId).SendAsync("Joined", me);
        return new { me, others, census = _roster.Census() };
    }

    public async Task Say(string text)
    {
        var me = _roster.Get(Context.ConnectionId);
        if (me is null) return;
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 500) text = text[..500];
        var line = new ChatLine { FromId = me.Id, From = me.Callsign, Text = text, Kind = "say" };
        await Clients.Group(me.Net).SendAsync("Chat", line);
    }

    public async Task Whisper(string toId, string text)
    {
        var me = _roster.Get(Context.ConnectionId);
        var them = _roster.Get(toId);
        if (me is null || them is null) return;
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 500) text = text[..500];
        var line = new ChatLine { FromId = me.Id, From = me.Callsign, To = them.Callsign, Text = text, Kind = "whisper" };
        await Clients.Client(toId).SendAsync("Chat", line);
        await Clients.Caller.SendAsync("Chat", line);
    }

    public async Task Media(bool mic, bool cam)
    {
        var me = _roster.Get(Context.ConnectionId);
        if (me is null) return;
        me.Mic = mic;
        me.Cam = cam;
        await Clients.Group(me.Net).SendAsync("Media", me.Id, mic, cam);
    }

    public async Task Talking(bool on)
    {
        var me = _roster.Get(Context.ConnectionId);
        if (me is null) return;
        me.Talking = on;
        await Clients.OthersInGroup(me.Net).SendAsync("Talking", me.Id, on);
    }

    public async Task Signal(string toId, string kind, string payload)
    {
        if (_roster.Get(Context.ConnectionId) is null) return;
        await Clients.Client(toId).SendAsync("Signal", Context.ConnectionId, kind, payload);
    }
}
