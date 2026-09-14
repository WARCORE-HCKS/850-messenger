using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;

namespace Area850.Services;

public sealed class HubClient : IAsyncDisposable
{
    private HubConnection? _hub;
    public event Action<JsonElement>? Message;
    public event Action<string, JsonElement>? History;
    public event Action<string, string, string>? Presence;
    public event Action<string>? SystemLine;
    public event Action<string>? Nudged;
    public event Action<string, string, string, string>? Signal;
    public event Action<JsonElement>? Whisper;
    public event Action<string, string, string>? Friend;
    public event Action<string>? Unfriend;
    public event Action<string, JsonElement>? RoomRoster;
    public event Action<string, string, string, bool>? RoomJoined;
    public event Action<string, string>? RoomLeft;
    public event Action<string, string, string, bool>? RoomCam;
    public event Action<string, string, string, bool>? RoomMic;
    public event Action<string, JsonElement>? RoomInfo;
    public event Action<string, string>? Kicked;
    public event Action<string, string, string>? Role;
    public event Action<string, string>? AuthAsk;
    public event Action<string, string, string, bool>? PeerTyping;
    public event Action<string, string>? StatusMsg;
    public event Action<string, string, string, long, string>? IncomingFile;
    public event Action<string, string, bool>? FileAnswer;
    public event Action<string, string, int, int, string>? FilePart;

    public async Task Connect(string baseUrl, string token)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(baseUrl.TrimEnd('/') + "/hub?access_token=" + Uri.EscapeDataString(token))
            .WithAutomaticReconnect()
            .Build();
        _hub.On<JsonElement>("Message", m => Message?.Invoke(m));
        _hub.On<string, JsonElement>("History", (r, h) => History?.Invoke(r, h));
        _hub.On<string, string, string>("Presence", (u, d, s) => Presence?.Invoke(u, d, s));
        _hub.On<string, string>("System", (_, t) => SystemLine?.Invoke(t));
        _hub.On<string>("Nudge", who => Nudged?.Invoke(who));
        _hub.On<string, string, string, string>("Signal", (u, d, k, p) => Signal?.Invoke(u, d, k, p));
        _hub.On<JsonElement>("Whisper", w => Whisper?.Invoke(w));
        _hub.On<string, string, string>("Friend", (u, d, s) => Friend?.Invoke(u, d, s));
        _hub.On<string>("Unfriend", u => Unfriend?.Invoke(u));
        _hub.On<string, JsonElement>("RoomRoster", (r, m) => RoomRoster?.Invoke(r, m));
        _hub.On<string, string, string, bool>("RoomJoined", (r, u, d, c) => RoomJoined?.Invoke(r, u, d, c));
        _hub.On<string, string>("RoomLeft", (r, u) => RoomLeft?.Invoke(r, u));
        _hub.On<string, string, string, bool>("RoomCam", (r, u, d, c) => RoomCam?.Invoke(r, u, d, c));
        _hub.On<string, string, string, bool>("RoomMic", (r, u, d, c) => RoomMic?.Invoke(r, u, d, c));
        _hub.On<string, JsonElement>("RoomInfo", (r, i) => RoomInfo?.Invoke(r, i));
        _hub.On<string, string>("Kicked", (r, why) => Kicked?.Invoke(r, why));
        _hub.On<string, string, string>("Role", (r, u, role) => Role?.Invoke(r, u, role));
        _hub.On<string, string>("AuthAsk", (u, d) => AuthAsk?.Invoke(u, d));
        _hub.On<string, string, string, bool>("Typing", (r, u, d, on) => PeerTyping?.Invoke(r, u, d, on));
        _hub.On<string, string>("StatusMsg", (u, m) => StatusMsg?.Invoke(u, m));
        _hub.On<string, string, string, long, string>("FileOffer", (u, d, n, sz, id) => IncomingFile?.Invoke(u, d, n, sz, id));
        _hub.On<string, string, bool>("FileDecide", (u, id, ok) => FileAnswer?.Invoke(u, id, ok));
        _hub.On<string, string, int, int, string>("FileChunk", (u, id, i, t, data) => FilePart?.Invoke(u, id, i, t, data));
        await _hub.StartAsync();
    }

    public Task<WhoDto> Who() => _hub!.InvokeAsync<WhoDto>("Who");
    public Task JoinRoom(string slug, string? password = null) => _hub!.InvokeAsync("JoinRoom", slug, password ?? "");
    public Task LeaveRoom(string slug) => _hub!.InvokeAsync("LeaveRoom", slug);
    public Task Send(string room, string body, string kind = "say") => _hub!.InvokeAsync("Send", room, body, kind);
    public Task WhisperTo(string user, string body) => _hub!.InvokeAsync("Whisper", user, body);
    public Task Nudge(string? user) => _hub!.InvokeAsync("Nudge", user);
    public Task SignalTo(string user, string kind, string payload) => _hub!.InvokeAsync("Signal", user, kind, payload);
    public Task SetStatus(string status) => _hub!.InvokeAsync("SetStatus", status);
    public Task SetInvisible(bool on) => _hub!.InvokeAsync("SetInvisible", on);
    public Task<JsonElement> Stats() => _hub!.InvokeAsync<JsonElement>("Stats");
    public Task<JsonElement> AdminOverview() => _hub!.InvokeAsync<JsonElement>("AdminOverview");
    public Task<JsonElement> AdminUsers(string q) => _hub!.InvokeAsync<JsonElement>("AdminUsers", q);
    public Task<JsonElement> AdminRooms() => _hub!.InvokeAsync<JsonElement>("AdminRooms");
    public Task<JsonElement> AdminMessages(string? room, int take = 80) => _hub!.InvokeAsync<JsonElement>("AdminMessages", room, take);
    public Task AdminSetPassword(string uin, string password) => _hub!.InvokeAsync("AdminSetPassword", uin, password);
    public Task AdminKickUser(string uin) => _hub!.InvokeAsync("AdminKickUser", uin);
    public Task AdminDisable(string uin, bool on) => _hub!.InvokeAsync("AdminDisable", uin, on);
    public Task AdminDeleteUser(string uin) => _hub!.InvokeAsync("AdminDeleteUser", uin);
    public Task AdminLockRoom(string slug, bool on) => _hub!.InvokeAsync("AdminLockRoom", slug, on);
    public Task AdminClearPass(string slug) => _hub!.InvokeAsync("AdminClearPass", slug);
    public Task AdminSay(string slug, string body) => _hub!.InvokeAsync("AdminSay", slug, body);
    public Task AdminDeleteRoom(string slug) => _hub!.InvokeAsync("AdminDeleteRoom", slug);
    public Task AddFriend(string username) => _hub!.InvokeAsync("AddFriend", username);
    public Task AuthReply(string fromUin, bool accept) => _hub!.InvokeAsync("AuthReply", fromUin, accept);
    public Task RemoveFriend(string uin) => _hub!.InvokeAsync("RemoveFriend", uin);
    public Task Block(string uin) => _hub!.InvokeAsync("Block", uin);
    public Task Typing(string room, bool on) => _hub!.InvokeAsync("Typing", room, on);
    public Task SetRoomCam(string slug, bool on) => _hub!.InvokeAsync("RoomCam", slug, on);
    public Task SetRoomMic(string slug, bool on) => _hub!.InvokeAsync("RoomMic", slug, on);
    public Task<JsonElement> CreateRoom(string title, string channel, string? password, string topic, string welcome) =>
        _hub!.InvokeAsync<JsonElement>("CreateRoom", title, channel, password, topic, welcome);
    public Task SetRoomMeta(string slug, string topic, string welcome, string? password, bool doorLock, bool slow) =>
        _hub!.InvokeAsync("SetRoomMeta", slug, topic, welcome, password, doorLock, slow);
    public Task SetRoomRole(string slug, string uin, string role) => _hub!.InvokeAsync("SetRoomRole", slug, uin, role);
    public Task KickRoom(string slug, string uin, string reason) => _hub!.InvokeAsync("KickRoom", slug, uin, reason);
    public Task BanRoom(string slug, string uin) => _hub!.InvokeAsync("BanRoom", slug, uin);
    public Task<JsonElement> GetProfile(string uin) => _hub!.InvokeAsync<JsonElement>("GetProfile", uin);
    public Task SetProfile(string display, string about, string statusMsg, string city, string gender) =>
        _hub!.InvokeAsync("SetProfile", display, about, statusMsg, city, gender);
    public Task<JsonElement> Find(string query) => _hub!.InvokeAsync<JsonElement>("Find", query);
    public Task FileOffer(string to, string name, long size, string id) => _hub!.InvokeAsync("FileOffer", to, name, size, id);
    public Task FileDecide(string to, string id, bool accept) => _hub!.InvokeAsync("FileDecide", to, id, accept);
    public Task FileChunk(string to, string id, int index, int total, string data) =>
        _hub!.InvokeAsync("FileChunk", to, id, index, total, data);

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}

public sealed class WhoDto
{
    public MeDto Me { get; set; } = new();
    public List<RoomDto> Rooms { get; set; } = new();
    public List<OnlineDto> Online { get; set; } = new();
    public List<FriendDto> Friends { get; set; } = new();
    public List<AuthDto> PendingAuth { get; set; } = new();
    public List<MailDto> Mail { get; set; } = new();
}
public sealed class AuthDto
{
    public string From { get; set; } = "";
    public string Display { get; set; } = "";
}
public sealed class MailDto
{
    public string From { get; set; } = "";
    public string Display { get; set; } = "";
    public string Body { get; set; } = "";
    public string At { get; set; } = "";
}
public sealed class FriendDto
{
    public string Username { get; set; } = "";
    public string Display { get; set; } = "";
    public string Status { get; set; } = "";
}
public sealed class MeDto
{
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string Display { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool Invisible { get; set; }
}
public sealed class RoomDto
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Channel { get; set; } = "hangout";
    public string Topic { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Welcome { get; set; } = "";
    public bool Locked { get; set; }
    public bool DoorLock { get; set; }
    public int Users { get; set; }
    public bool Hot { get; set; }
}
public sealed class OnlineDto
{
    public string Username { get; set; } = "";
    public string Display { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Ghost { get; set; }
}
