using System.Collections.Concurrent;
using System.Linq;

namespace Area850.Net;

public sealed class Roster
{
    public static readonly string[] Nets =
    [
        "Lobby",
        "Area 850",
        "War Room",
        "Night Watch",
        "Off-Comms"
    ];

    private readonly ConcurrentDictionary<string, Member> _byId = new();

    public Member? Get(string id) => _byId.TryGetValue(id, out var m) ? m : null;

    public Member Add(string id, string callsign, string net)
    {
        var m = new Member { Id = id, Callsign = Sanitize(callsign), Net = net };
        _byId[id] = m;
        return m;
    }

    public Member? Remove(string id)
    {
        _byId.TryRemove(id, out var m);
        return m;
    }

    public IReadOnlyList<Member> InNet(string net) =>
        _byId.Values.Where(m => m.Net.Equals(net, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Callsign, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyDictionary<string, int> Census() =>
        Nets.ToDictionary(n => n, n => _byId.Values.Count(m => m.Net == n), StringComparer.OrdinalIgnoreCase);

    public static string Sanitize(string callsign)
    {
        var s = new string((callsign ?? "").Trim().Where(c => !char.IsControl(c)).ToArray());
        if (s.Length < 2) s = "Guest-" + Random.Shared.Next(10, 99);
        if (s.Length > 18) s = s[..18];
        return s;
    }
}
