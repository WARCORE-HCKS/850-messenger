using System.Collections.Concurrent;

namespace Area850.Server.Auth;

public sealed record Session(string Token, long UserId, string Username, string Display, bool IsAdmin, bool Invisible);

public sealed class Sessions
{
    private readonly ConcurrentDictionary<string, Session> _map = new();

    public Session Issue(long userId, string username, string display, bool isAdmin, bool invisible)
    {
        var s = new Session(Guid.NewGuid().ToString("N"), userId, username, display, isAdmin, invisible);
        _map[s.Token] = s;
        return s;
    }

    public Session? UpdateInvisible(string token, bool invisible)
    {
        if (!_map.TryGetValue(token, out var s)) return s;
        var n = s with { Invisible = invisible };
        _map[token] = n;
        return n;
    }

    public Session? UpdateDisplay(string token, string display)
    {
        if (!_map.TryGetValue(token, out var s)) return s;
        var n = s with { Display = display };
        _map[token] = n;
        return n;
    }

    public Session? Get(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : _map.GetValueOrDefault(token);

    public void DropUser(string username)
    {
        foreach (var kv in _map)
            if (kv.Value.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                _map.TryRemove(kv.Key, out _);
    }
}
