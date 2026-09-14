using Area850.Server;
using Area850.Server.Auth;
using Area850.Server.Data;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Area850.Server.Hubs;

public sealed class ChatHub : Hub
{
    private readonly Db _db;
    private readonly Sessions _sessions;
    private readonly AdminOps _admin;
    private static readonly ConcurrentDictionary<string, Presence> Online = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Seat>> Rooms = new();
    private static readonly ConcurrentDictionary<(string from, string to), byte> AutoOnce = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastSaid = new();
    private static readonly HashSet<string> Channels = new(StringComparer.OrdinalIgnoreCase)
        { "hangout", "music", "tech", "gaming", "dating", "afterdark", "area850", "war" };
    private static readonly HashSet<string> Statuses = new(StringComparer.OrdinalIgnoreCase)
        { "online", "away", "occupied", "dnd", "invisible" };

    public ChatHub(Db db, Sessions sessions, AdminOps admin)
    {
        _db = db;
        _sessions = sessions;
        _admin = admin;
    }

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["access_token"].ToString();
        var s = _sessions.Get(token);
        if (s is null) throw new HubException("Sign in first.");
        Context.Items["session"] = s;
        var ghost = s.Invisible;
        Online[s.Username] = new Presence(s.UserId, s.Username, s.Display, ghost ? "invisible" : "online", Context.ConnectionId, s.IsAdmin, ghost);
        if (!ghost)
            await Clients.Others.SendAsync("Presence", s.Username, s.Display, "online");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        if (Me() is { } s)
        {
            var wasGhost = Online.TryGetValue(s.Username, out var p) && p.Invisible;
            Online.TryRemove(s.Username, out _);
            foreach (var kv in Rooms)
            {
                if (kv.Value.TryRemove(s.Username, out var seat) && !seat.Ghost)
                    await Clients.OthersInGroup("room:" + kv.Key).SendAsync("RoomLeft", kv.Key, s.Username);
            }
            if (!wasGhost)
                await Clients.Others.SendAsync("Presence", s.Username, s.Display, "offline");
        }
        await base.OnDisconnectedAsync(ex);
    }

    public object Who()
    {
        var s = Need();
        var people = Online.Values
            .Where(p => s.IsAdmin || !p.Invisible)
            .Select(p => new { p.Username, p.Display, Status = p.Invisible ? "invisible" : p.Status, ghost = p.Invisible && s.IsAdmin });
        return new
        {
            me = new { s.UserId, s.Username, s.Display, s.IsAdmin, s.Invisible },
            rooms = ListRooms(),
            online = people.ToList(),
            friends = ListFriends(s.UserId),
            pendingAuth = ListAuth(s.Username),
            mail = TakeMail(s.Username)
        };
    }

    public async Task AddFriend(string username)
    {
        var s = Need();
        username = Norm(username);
        if (username.Length < 1 || username.Equals(s.Username, StringComparison.OrdinalIgnoreCase))
            throw new HubException("Enter their 850 number.");
        using var c = _db.Open();
        using var exists = c.CreateCommand();
        exists.CommandText = "SELECT display, username FROM users WHERE username=$u OR CAST(uin AS TEXT)=$u";
        exists.Parameters.AddWithValue("$u", username);
        using var er = exists.ExecuteReader();
        if (!er.Read()) throw new HubException("No one with that number.");
        var display = er.GetString(0);
        username = er.GetString(1);
        er.Close();
        if (Blocked(UserIdOf(username), s.Username))
            throw new HubException("They are not accepting requests.");
        using var already = c.CreateCommand();
        already.CommandText = "SELECT COALESCE(pending,0) FROM friends WHERE user_id=$i AND friend_username=$u";
        already.Parameters.AddWithValue("$i", s.UserId);
        already.Parameters.AddWithValue("$u", username);
        var have = already.ExecuteScalar();
        if (have is long or int)
        {
            if (Convert.ToInt64(have) == 0) throw new HubException("Already on your list.");
            throw new HubException("Waiting for authorization.");
        }
        using var ins = c.CreateCommand();
        ins.CommandText = "INSERT OR IGNORE INTO friends(user_id, friend_username, pending) VALUES($i,$u,1)";
        ins.Parameters.AddWithValue("$i", s.UserId);
        ins.Parameters.AddWithValue("$u", username);
        ins.ExecuteNonQuery();
        using var req = c.CreateCommand();
        req.CommandText = "INSERT OR REPLACE INTO auth_req(from_uin,from_display,to_uin,note,created) VALUES($f,$d,$t,$n,$c)";
        req.Parameters.AddWithValue("$f", s.Username);
        req.Parameters.AddWithValue("$d", s.Display);
        req.Parameters.AddWithValue("$t", username);
        req.Parameters.AddWithValue("$n", "");
        req.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
        req.ExecuteNonQuery();
        await Clients.Caller.SendAsync("Friend", username, display, "awaiting");
        if (Online.TryGetValue(username, out var them) && !Blocked(them.UserId, s.Username))
            await Clients.Client(them.ConnectionId).SendAsync("AuthAsk", s.Username, s.Display);
        await Clients.Caller.SendAsync("System", "", "Authorization requested for " + display + " (#" + username + ").");
    }

    public async Task AuthReply(string fromUin, bool accept)
    {
        var s = Need();
        fromUin = Norm(fromUin);
        using var c = _db.Open();
        using var del = c.CreateCommand();
        del.CommandText = "DELETE FROM auth_req WHERE from_uin=$f AND to_uin=$t";
        del.Parameters.AddWithValue("$f", fromUin);
        del.Parameters.AddWithValue("$t", s.Username);
        del.ExecuteNonQuery();
        using var find = c.CreateCommand();
        find.CommandText = "SELECT id, display FROM users WHERE username=$u";
        find.Parameters.AddWithValue("$u", fromUin);
        using var fr = find.ExecuteReader();
        if (!fr.Read()) return;
        var fromId = fr.GetInt64(0);
        var fromDisplay = fr.GetString(1);
        fr.Close();
        if (!accept)
        {
            using var rm = c.CreateCommand();
            rm.CommandText = "DELETE FROM friends WHERE user_id=$i AND friend_username=$u";
            rm.Parameters.AddWithValue("$i", fromId);
            rm.Parameters.AddWithValue("$u", s.Username);
            rm.ExecuteNonQuery();
            if (Online.TryGetValue(fromUin, out var denied))
            {
                await Clients.Client(denied.ConnectionId).SendAsync("Unfriend", s.Username);
                await Clients.Client(denied.ConnectionId).SendAsync("System", "", s.Display + " declined authorization.");
            }
            return;
        }
        using var up = c.CreateCommand();
        up.CommandText = "UPDATE friends SET pending=0 WHERE user_id=$i AND friend_username=$u";
        up.Parameters.AddWithValue("$i", fromId);
        up.Parameters.AddWithValue("$u", s.Username);
        up.ExecuteNonQuery();
        using var back = c.CreateCommand();
        back.CommandText = "INSERT OR REPLACE INTO friends(user_id, friend_username, pending) VALUES($i,$u,0)";
        back.Parameters.AddWithValue("$i", s.UserId);
        back.Parameters.AddWithValue("$u", fromUin);
        back.ExecuteNonQuery();
        var st = Online.TryGetValue(fromUin, out var p) && !p.Invisible ? p.Status : "offline";
        await Clients.Caller.SendAsync("Friend", fromUin, fromDisplay, st);
        if (Online.TryGetValue(fromUin, out var them))
        {
            await Clients.Client(them.ConnectionId).SendAsync("Friend", s.Username, s.Display, s.Invisible ? "offline" : (Online.TryGetValue(s.Username, out var mep) ? mep.Status : "online"));
            await Clients.Client(them.ConnectionId).SendAsync("System", "", s.Display + " authorized you.");
        }
    }

    public async Task RemoveFriend(string uin)
    {
        var s = Need();
        uin = Norm(uin);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM friends WHERE user_id=$i AND friend_username=$u";
        cmd.Parameters.AddWithValue("$i", s.UserId);
        cmd.Parameters.AddWithValue("$u", uin);
        cmd.ExecuteNonQuery();
        await Clients.Caller.SendAsync("Unfriend", uin);
    }

    public async Task Block(string uin)
    {
        var s = Need();
        uin = Norm(uin);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO blocks(user_id, blocked) VALUES($i,$u)";
        cmd.Parameters.AddWithValue("$i", s.UserId);
        cmd.Parameters.AddWithValue("$u", uin);
        cmd.ExecuteNonQuery();
        using var rm = c.CreateCommand();
        rm.CommandText = "DELETE FROM friends WHERE user_id=$i AND friend_username=$u";
        rm.Parameters.AddWithValue("$i", s.UserId);
        rm.Parameters.AddWithValue("$u", uin);
        rm.ExecuteNonQuery();
        await Clients.Caller.SendAsync("Unfriend", uin);
        await Clients.Caller.SendAsync("Blocked", uin);
    }

    public Task Typing(string room, bool on)
    {
        var s = Need();
        if (room.StartsWith("dm:"))
        {
            var other = OtherInDm(room, s.Username);
            if (other is not null && Online.TryGetValue(other, out var them) && !Blocked(them.UserId, s.Username))
                return Clients.Client(them.ConnectionId).SendAsync("Typing", room, s.Username, s.Display, on);
            return Task.CompletedTask;
        }
        return Clients.OthersInGroup("room:" + room).SendAsync("Typing", room, s.Username, s.Display, on);
    }

    public object Find(string query)
    {
        Need();
        query = (query ?? "").Trim();
        if (query.Length < 1) return Array.Empty<object>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT username, display, uin FROM users
            WHERE username=$q OR CAST(uin AS TEXT)=$q OR display LIKE $like
            ORDER BY uin LIMIT 20
            """;
        cmd.Parameters.AddWithValue("$q", Norm(query));
        cmd.Parameters.AddWithValue("$like", "%" + query + "%");
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
        {
            var uname = r.GetString(0);
            var online = Online.TryGetValue(uname, out var p) && !p.Invisible;
            list.Add(new
            {
                uin = r.IsDBNull(2) ? uname : r.GetInt64(2).ToString(),
                username = uname,
                display = r.GetString(1),
                status = online ? p.Status : "offline"
            });
        }
        return list;
    }

    public object GetProfile(string uin)
    {
        Need();
        uin = Norm(uin);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT u.username, u.display, u.uin, p.about, p.status_msg, p.city, p.gender
            FROM users u LEFT JOIN profiles p ON p.user_id=u.id
            WHERE u.username=$u OR CAST(u.uin AS TEXT)=$u
            """;
        cmd.Parameters.AddWithValue("$u", uin);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new HubException("No such UIN.");
        var uname = r.GetString(0);
        var online = Online.TryGetValue(uname, out var p) && !p.Invisible;
        return new
        {
            uin = r.IsDBNull(2) ? uname : r.GetInt64(2).ToString(),
            username = uname,
            display = r.GetString(1),
            about = r.IsDBNull(3) ? "" : r.GetString(3),
            statusMsg = r.IsDBNull(4) ? "" : r.GetString(4),
            city = r.IsDBNull(5) ? "" : r.GetString(5),
            gender = r.IsDBNull(6) ? "" : r.GetString(6),
            status = online ? p.Status : "offline"
        };
    }

    public async Task SetProfile(string display, string about, string statusMsg, string city, string gender)
    {
        var s = Need();
        display = (display ?? "").Trim();
        if (display.Length < 2 || display.Length > 24) throw new HubException("Nickname 2–24 characters.");
        using var c = _db.Open();
        using var nick = c.CreateCommand();
        nick.CommandText = "UPDATE users SET display=$d WHERE id=$i";
        nick.Parameters.AddWithValue("$d", display);
        nick.Parameters.AddWithValue("$i", s.UserId);
        nick.ExecuteNonQuery();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profiles(user_id, about, status_msg, city, gender)
            VALUES($i,$a,$m,$c,$g)
            ON CONFLICT(user_id) DO UPDATE SET about=$a, status_msg=$m, city=$c, gender=$g
            """;
        cmd.Parameters.AddWithValue("$i", s.UserId);
        cmd.Parameters.AddWithValue("$a", (about ?? "").Trim());
        cmd.Parameters.AddWithValue("$m", (statusMsg ?? "").Trim());
        cmd.Parameters.AddWithValue("$c", (city ?? "").Trim());
        cmd.Parameters.AddWithValue("$g", (gender ?? "").Trim());
        cmd.ExecuteNonQuery();
        var token = Context.GetHttpContext()?.Request.Query["access_token"].ToString() ?? "";
        var updated = _sessions.UpdateDisplay(token, display) ?? s;
        Context.Items["session"] = updated;
        if (Online.TryGetValue(s.Username, out var p))
        {
            Online[s.Username] = p with { Display = display };
            if (!p.Invisible)
                await Clients.Others.SendAsync("StatusMsg", s.Username, statusMsg ?? "");
        }
        await Clients.Caller.SendAsync("System", "", "Profile saved.");
    }

    public async Task JoinRoom(string slug, string? password = null)
    {
        var s = Need();
        var ghost = Online.TryGetValue(s.Username, out var p) && p.Invisible;
        slug = slug.Trim().ToLowerInvariant();
        var dm = slug.StartsWith("dm:");
        RoomMeta? meta = null;
        var myRole = "member";
        if (!dm)
        {
            meta = LoadRoom(slug) ?? throw new HubException("No such room.");
            myRole = GetRole(slug, s.Username, s.IsAdmin, meta.Owner);
            if (myRole == "banned" && !s.IsAdmin) throw new HubException("You're banned from this room.");
            if (meta.DoorLock && Rank(myRole) < 2 && !s.IsAdmin)
                throw new HubException("Door is locked. Mods only.");
            if (meta.HasPass && Rank(myRole) < 2 && !s.IsAdmin && !ghost && !VerifyRoomPass(meta, password))
                throw new HubException("Password locked. Wrong key.");
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, "room:" + slug);
        var bag = Rooms.GetOrAdd(slug, _ => new ConcurrentDictionary<string, Seat>(StringComparer.OrdinalIgnoreCase));
        bag[s.Username] = new Seat(s.Username, s.Display, Context.ConnectionId, false, ghost, false);
        var visible = bag.Values.Where(x => !x.Ghost).Select(x => new { x.Username, x.Display, x.Cam, x.Mic, role = dm ? "member" : GetRole(slug, x.Username, false, meta?.Owner ?? "") }).ToList();
        await Clients.Caller.SendAsync("RoomRoster", slug, visible);
        if (!dm)
        {
            await Clients.Caller.SendAsync("RoomInfo", slug, InfoPayload(meta!, myRole, bag.Values.Count(x => !x.Ghost)));
            if (!string.IsNullOrWhiteSpace(meta!.Welcome))
                await Clients.Caller.SendAsync("System", slug, meta.Welcome);
        }
        if (ghost)
            await Clients.Caller.SendAsync("System", slug, "You entered invisibly. Nobody on this room can see you unless you talk.");
        else
        {
            await Clients.OthersInGroup("room:" + slug).SendAsync("RoomJoined", slug, s.Username, s.Display, false);
            await Clients.Group("room:" + slug).SendAsync("System", slug, $"{s.Display} is here.");
        }
        await Clients.Caller.SendAsync("History", slug, History(slug, 80));
    }

    public async Task RoomCam(string slug, bool on)
    {
        var s = Need();
        if (!Rooms.TryGetValue(slug, out var bag) || !bag.TryGetValue(s.Username, out var seat)) return;
        if (GetRole(slug, s.Username, s.IsAdmin, LoadRoom(slug)?.Owner ?? "") == "muted")
            throw new HubException("You're muted.");
        if (seat.Ghost && on) throw new HubException("Turn off invisible before going on camera.");
        bag[s.Username] = seat with { Cam = on };
        await Clients.Group("room:" + slug).SendAsync("RoomCam", slug, s.Username, s.Display, on);
    }

    public async Task RoomMic(string slug, bool on)
    {
        var s = Need();
        if (!Rooms.TryGetValue(slug, out var bag) || !bag.TryGetValue(s.Username, out var seat)) return;
        if (GetRole(slug, s.Username, s.IsAdmin, LoadRoom(slug)?.Owner ?? "") == "muted")
            throw new HubException("You're muted.");
        if (seat.Ghost && on) throw new HubException("Turn off invisible before talking.");
        bag[s.Username] = seat with { Mic = on };
        await Clients.Group("room:" + slug).SendAsync("RoomMic", slug, s.Username, s.Display, on);
    }

    public async Task<object> CreateRoom(string title, string channel, string? password, string topic, string welcome)
    {
        var s = Need();
        title = (title ?? "").Trim();
        if (title.Length < 2 || title.Length > 32) throw new HubException("Room name 2–32 characters.");
        channel = (channel ?? "hangout").Trim().ToLowerInvariant();
        if (!Channels.Contains(channel)) channel = "hangout";
        topic = (topic ?? "").Trim();
        if (topic.Length > 80) topic = topic[..80];
        welcome = (welcome ?? "").Trim();
        if (welcome.Length > 240) welcome = welcome[..240];
        var slug = Slugify(title);
        using var c = _db.Open();
        using (var exists = c.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM rooms WHERE slug=$s";
            exists.Parameters.AddWithValue("$s", slug);
            if (exists.ExecuteScalar() is not null)
                slug = slug + "-" + Random.Shared.Next(10, 99);
        }
        byte[]? salt = null, hash = null;
        if (!string.IsNullOrWhiteSpace(password))
            (salt, hash) = Db.HashPassword(password);
        using var ins = c.CreateCommand();
        ins.CommandText = """
            INSERT INTO rooms(slug,title,kind,channel,owner,topic,welcome,pass_salt,pass_hash)
            VALUES($s,$t,'public',$ch,$o,$p,$w,$sa,$h)
            """;
        ins.Parameters.AddWithValue("$s", slug);
        ins.Parameters.AddWithValue("$t", title);
        ins.Parameters.AddWithValue("$ch", channel);
        ins.Parameters.AddWithValue("$o", s.Username);
        ins.Parameters.AddWithValue("$p", topic);
        ins.Parameters.AddWithValue("$w", welcome);
        ins.Parameters.AddWithValue("$sa", (object?)salt ?? DBNull.Value);
        ins.Parameters.AddWithValue("$h", (object?)hash ?? DBNull.Value);
        ins.ExecuteNonQuery();
        using var role = c.CreateCommand();
        role.CommandText = "INSERT INTO room_roles(slug,username,role) VALUES($s,$u,'owner')";
        role.Parameters.AddWithValue("$s", slug);
        role.Parameters.AddWithValue("$u", s.Username);
        role.ExecuteNonQuery();
        await Clients.All.SendAsync("System", "", s.Display + " opened a room in " + ChannelLabel(channel) + ": " + title);
        return new { slug, title, channel };
    }

    public async Task SetRoomMeta(string slug, string topic, string welcome, string? password, bool doorLock, bool slow)
    {
        var s = Need();
        slug = slug.Trim().ToLowerInvariant();
        var meta = LoadRoom(slug) ?? throw new HubException("No such room.");
        if (Rank(GetRole(slug, s.Username, s.IsAdmin, meta.Owner)) < 3)
            throw new HubException("Only the room owner or a room admin can do that.");
        topic = (topic ?? "").Trim();
        if (topic.Length > 80) topic = topic[..80];
        welcome = (welcome ?? "").Trim();
        if (welcome.Length > 240) welcome = welcome[..240];
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        if (password is not null)
        {
            if (password.Length == 0)
            {
                cmd.CommandText = "UPDATE rooms SET topic=$p, welcome=$w, locked=$l, slow=$s, pass_salt=NULL, pass_hash=NULL WHERE slug=$slug";
            }
            else
            {
                var (salt, hash) = Db.HashPassword(password);
                cmd.CommandText = "UPDATE rooms SET topic=$p, welcome=$w, locked=$l, slow=$s, pass_salt=$sa, pass_hash=$h WHERE slug=$slug";
                cmd.Parameters.AddWithValue("$sa", salt);
                cmd.Parameters.AddWithValue("$h", hash);
            }
        }
        else
            cmd.CommandText = "UPDATE rooms SET topic=$p, welcome=$w, locked=$l, slow=$s WHERE slug=$slug";
        cmd.Parameters.AddWithValue("$p", topic);
        cmd.Parameters.AddWithValue("$w", welcome);
        cmd.Parameters.AddWithValue("$l", doorLock ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", slow ? 1 : 0);
        cmd.Parameters.AddWithValue("$slug", slug);
        cmd.ExecuteNonQuery();
        meta = LoadRoom(slug)!;
        var n = Rooms.TryGetValue(slug, out var bag) ? bag.Values.Count(x => !x.Ghost) : 0;
        await Clients.Group("room:" + slug).SendAsync("RoomInfo", slug, InfoPayload(meta, GetRole(slug, s.Username, s.IsAdmin, meta.Owner), n));
        await Clients.Group("room:" + slug).SendAsync("System", slug, "Room settings updated.");
    }

    public async Task SetRoomRole(string slug, string uin, string role)
    {
        var s = Need();
        slug = slug.Trim().ToLowerInvariant();
        uin = Norm(uin);
        role = (role ?? "member").Trim().ToLowerInvariant();
        if (role is not ("admin" or "mod" or "member" or "muted")) throw new HubException("Unknown rank.");
        var meta = LoadRoom(slug) ?? throw new HubException("No such room.");
        var mine = GetRole(slug, s.Username, s.IsAdmin, meta.Owner);
        var theirs = GetRole(slug, uin, false, meta.Owner);
        if (theirs == "owner") throw new HubException("Can't demote the room owner.");
        if (Rank(mine) < 3 || Rank(mine) <= Rank(theirs)) throw new HubException("You don't outrank them.");
        if (role == "admin" && Rank(mine) < 4 && !s.IsAdmin) throw new HubException("Only the owner can mint room admins.");
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO room_roles(slug,username,role) VALUES($s,$u,$r) ON CONFLICT(slug,username) DO UPDATE SET role=$r";
        cmd.Parameters.AddWithValue("$s", slug);
        cmd.Parameters.AddWithValue("$u", uin);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.ExecuteNonQuery();
        var label = role switch { "admin" => "room admin", "mod" => "mod", "muted" => "muted", _ => "member" };
        await Clients.Group("room:" + slug).SendAsync("System", slug, s.Display + " set " + uin + " to " + label + ".");
        await Clients.Group("room:" + slug).SendAsync("Role", slug, uin, role);
    }

    public async Task KickRoom(string slug, string uin, string reason)
    {
        var s = Need();
        slug = slug.Trim().ToLowerInvariant();
        uin = Norm(uin);
        var meta = LoadRoom(slug) ?? throw new HubException("No such room.");
        if (Rank(GetRole(slug, s.Username, s.IsAdmin, meta.Owner)) < 2) throw new HubException("Mods only.");
        if (Rank(GetRole(slug, s.Username, s.IsAdmin, meta.Owner)) <= Rank(GetRole(slug, uin, false, meta.Owner)))
            throw new HubException("You don't outrank them.");
        if (Rooms.TryGetValue(slug, out var bag) && bag.TryRemove(uin, out var seat))
        {
            await Groups.RemoveFromGroupAsync(seat.ConnectionId, "room:" + slug);
            await Clients.Client(seat.ConnectionId).SendAsync("Kicked", slug, string.IsNullOrWhiteSpace(reason) ? "Bounced." : reason);
        }
        await Clients.Group("room:" + slug).SendAsync("RoomLeft", slug, uin);
        await Clients.Group("room:" + slug).SendAsync("System", slug, s.Display + " bounced " + uin + ".");
    }

    public async Task BanRoom(string slug, string uin)
    {
        var s = Need();
        slug = slug.Trim().ToLowerInvariant();
        uin = Norm(uin);
        var meta = LoadRoom(slug) ?? throw new HubException("No such room.");
        if (Rank(GetRole(slug, s.Username, s.IsAdmin, meta.Owner)) < 3) throw new HubException("Room admin or owner only.");
        if (GetRole(slug, uin, false, meta.Owner) == "owner") throw new HubException("Can't ban the owner.");
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO room_roles(slug,username,role) VALUES($s,$u,'banned') ON CONFLICT(slug,username) DO UPDATE SET role='banned'";
        cmd.Parameters.AddWithValue("$s", slug);
        cmd.Parameters.AddWithValue("$u", uin);
        cmd.ExecuteNonQuery();
        if (Rooms.TryGetValue(slug, out var bag) && bag.TryRemove(uin, out var seat))
        {
            await Groups.RemoveFromGroupAsync(seat.ConnectionId, "room:" + slug);
            await Clients.Client(seat.ConnectionId).SendAsync("Kicked", slug, "Banned from this room.");
        }
        await Clients.Group("room:" + slug).SendAsync("RoomLeft", slug, uin);
        await Clients.Group("room:" + slug).SendAsync("System", slug, s.Display + " banned " + uin + ".");
    }

    public Task SetInvisible(bool on) => SetStatus(on ? "invisible" : "online");

    public async Task SetStatus(string status)
    {
        var s = Need();
        status = (status ?? "online").Trim().ToLowerInvariant();
        if (!Statuses.Contains(status)) status = "online";
        var token = Context.GetHttpContext()?.Request.Query["access_token"].ToString() ?? "";
        var ghost = status == "invisible";
        var updated = _sessions.UpdateInvisible(token, ghost) ?? s;
        Context.Items["session"] = updated;
        if (Online.TryGetValue(s.Username, out var p))
            Online[s.Username] = p with { Invisible = ghost, Status = status, Display = updated.Display };
        if (ghost)
            await Clients.Others.SendAsync("Presence", s.Username, s.Display, "offline");
        else
            await Clients.Others.SendAsync("Presence", s.Username, s.Display, status);
        await Clients.Caller.SendAsync("System", "", "Status: " + Label(status));
    }

    public object Stats()
    {
        var s = Need();
        if (!s.IsAdmin) throw new HubException("Admin only.");
        using var c = _db.Open();
        long Scalar(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v is long l ? l : Convert.ToInt64(v ?? 0);
        }
        var users = Scalar("SELECT COUNT(*) FROM users");
        var messages = Scalar("SELECT COUNT(*) FROM messages");
        var today = Scalar("SELECT COUNT(*) FROM messages WHERE created >= strftime('%Y-%m-%d','now')");
        var rooms = new List<object>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT r.title, r.slug, (SELECT COUNT(*) FROM messages m WHERE m.room=r.slug) AS n
                FROM rooms r ORDER BY n DESC
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rooms.Add(new { title = r.GetString(0), slug = r.GetString(1), messages = r.GetInt64(2) });
        }
        var recent = new List<object>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT display, room, body, created FROM messages ORDER BY id DESC LIMIT 20";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                recent.Add(new { display = r.GetString(0), room = r.GetString(1), body = r.GetString(2), at = r.GetString(3) });
        }
        return new
        {
            users,
            messages,
            today,
            online = Online.Count,
            visible = Online.Values.Count(p => !p.Invisible),
            ghosts = Online.Values.Count(p => p.Invisible),
            sessions = Online.Values.Select(p => new { p.Username, p.Display, p.Status, p.Invisible, p.IsAdmin }).ToList(),
            rooms,
            recent
        };
    }

    private Session NeedAdmin()
    {
        var s = Need();
        if (!s.IsAdmin) throw new HubException("Admin only.");
        return s;
    }

    private Task NotifyKick(string conn, string why) =>
        Clients.Client(conn).SendAsync("Kicked", "", why);

    public object AdminUsers(string q) { NeedAdmin(); return _admin.Users(q); }
    public object AdminRooms() { NeedAdmin(); return _admin.Rooms(); }
    public object AdminMessages(string? room, int take) { NeedAdmin(); return _admin.Messages(room, take); }
    public object AdminOverview() { NeedAdmin(); return _admin.Overview(); }

    public void AdminSetPassword(string uin, string password) { NeedAdmin(); _admin.SetPassword(uin, password); }
    public void AdminLockRoom(string slug, bool on) { NeedAdmin(); _admin.LockRoom(slug, on); }
    public void AdminClearPass(string slug) { NeedAdmin(); _admin.ClearPass(slug); }

    public Task AdminKickUser(string uin)
    {
        NeedAdmin();
        return _admin.Kick(uin, "Signed out by admin.", NotifyKick);
    }

    public Task AdminDisable(string uin, bool on)
    {
        var s = NeedAdmin();
        return _admin.Disable(uin, on, s.Username, NotifyKick);
    }

    public Task AdminDeleteUser(string uin)
    {
        NeedAdmin();
        return _admin.DeleteUser(uin, NotifyKick);
    }

    public async Task AdminSay(string slug, string body)
    {
        NeedAdmin();
        body = (body ?? "").Trim();
        if (body.Length == 0) return;
        await Clients.Group("room:" + slug).SendAsync("System", slug, "ADMIN: " + body);
    }

    public async Task AdminDeleteRoom(string slug)
    {
        NeedAdmin();
        await Clients.Group("room:" + slug).SendAsync("Kicked", slug, "Room closed by admin.");
        _admin.DeleteRoom(slug);
    }

    public static Dictionary<string, int> Occupancy()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Rooms)
            d[kv.Key] = kv.Value.Values.Count(x => !x.Ghost);
        return d;
    }

    public static (int online, int rooms, int sitting) AdminCounts()
    {
        var sitting = Rooms.Sum(kv => kv.Value.Values.Count(x => !x.Ghost));
        return (Online.Count, Rooms.Count(kv => kv.Value.Values.Any(x => !x.Ghost)), sitting);
    }

    public static object AdminLive() => new
    {
        online = Online.Count,
        visible = Online.Values.Count(p => !p.Invisible),
        ghosts = Online.Values.Count(p => p.Invisible),
        sessions = Online.Values.Select(p => new
        {
            username = p.Username,
            display = p.Display,
            status = p.Status,
            invisible = p.Invisible,
            isAdmin = p.IsAdmin
        }).ToList(),
        occupancy = Rooms.Select(kv => new
        {
            slug = kv.Key,
            n = kv.Value.Values.Count(x => !x.Ghost),
            people = kv.Value.Values.Select(s => new { username = s.Username, display = s.Display, cam = s.Cam, mic = s.Mic, ghost = s.Ghost }).ToList()
        }).ToList()
    };

    public static string? AdminDrop(string username)
    {
        string? conn = null;
        if (Online.TryRemove(username, out var p)) conn = p.ConnectionId;
        foreach (var kv in Rooms)
            kv.Value.TryRemove(username, out _);
        return conn;
    }

    public async Task LeaveRoom(string slug)
    {
        var s = Me();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "room:" + slug);
        if (s is null) return;
        if (Rooms.TryGetValue(slug, out var bag) && bag.TryRemove(s.Username, out var seat) && !seat.Ghost)
            await Clients.OthersInGroup("room:" + slug).SendAsync("RoomLeft", slug, s.Username);
    }

    public async Task Send(string room, string body, string kind = "say")
    {
        var s = Need();
        body = (body ?? "").Trim();
        if (body.Length == 0) return;
        if (body.Length > 800) body = body[..800];
        var at = DateTime.UtcNow.ToString("o");
        if (!room.StartsWith("dm:"))
        {
            var meta = LoadRoom(room);
            var role = GetRole(room, s.Username, s.IsAdmin, meta?.Owner ?? "");
            if (role == "muted") throw new HubException("You're muted in this room.");
            if (meta is { Slow: true } && Rank(role) < 2)
            {
                var key = room + ":" + s.Username;
                if (LastSaid.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(8))
                    throw new HubException("Slow mode — wait a few seconds.");
                LastSaid[key] = DateTime.UtcNow;
            }
        }
        string? other = room.StartsWith("dm:") ? OtherInDm(room, s.Username) : null;
        if (other is not null)
        {
            if (Blocked(s.UserId, other)) throw new HubException("You blocked this contact.");
            var oid = UserIdOf(other);
            if (oid > 0 && Blocked(oid, s.Username)) return;
        }
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO messages(room,user_id,display,body,kind,created) VALUES($r,$u,$d,$b,$k,$c)";
        cmd.Parameters.AddWithValue("$r", room);
        cmd.Parameters.AddWithValue("$u", s.UserId);
        cmd.Parameters.AddWithValue("$d", s.Display);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$c", at);
        cmd.ExecuteNonQuery();
        var payload = new
        {
            room,
            userId = s.UserId,
            username = s.Username,
            display = s.Display,
            body,
            kind,
            at
        };
        if (other is not null)
        {
            await Clients.Caller.SendAsync("Message", payload);
            if (Online.TryGetValue(other, out var them))
                await Clients.Client(them.ConnectionId).SendAsync("Message", payload);
            else
            {
                using var om = c.CreateCommand();
                om.CommandText = "INSERT INTO offline_mail(to_uin,from_uin,from_display,body,created) VALUES($t,$f,$d,$b,$c)";
                om.Parameters.AddWithValue("$t", other);
                om.Parameters.AddWithValue("$f", s.Username);
                om.Parameters.AddWithValue("$d", s.Display);
                om.Parameters.AddWithValue("$b", body);
                om.Parameters.AddWithValue("$c", at);
                om.ExecuteNonQuery();
            }
            if (Online.TryGetValue(other, out var live) && !live.Invisible && kind != "auto"
                && live.Status is "away" or "occupied" or "dnd")
            {
                var sm = StatusMsgOf(live.UserId);
                if (!string.IsNullOrWhiteSpace(sm) && AutoOnce.TryAdd((s.Username, other), 0))
                {
                    var auto = new
                    {
                        room,
                        userId = live.UserId,
                        username = live.Username,
                        display = live.Display,
                        body = sm,
                        kind = "auto",
                        at = DateTime.UtcNow.ToString("o")
                    };
                    await Clients.Caller.SendAsync("Message", auto);
                }
            }
        }
        else
            await Clients.Group("room:" + room).SendAsync("Message", payload);
    }

    public async Task FileOffer(string to, string name, long size, string id)
    {
        var s = Need();
        to = Norm(to);
        if (size <= 0 || size > 15L * 1024 * 1024) throw new HubException("File must be under 15 MB.");
        if (!Online.TryGetValue(to, out var them)) throw new HubException("They're offline. File send needs them online.");
        if (Blocked(them.UserId, s.Username) || Blocked(s.UserId, to)) return;
        await Clients.Client(them.ConnectionId).SendAsync("FileOffer", s.Username, s.Display, name, size, id);
    }

    public async Task FileDecide(string to, string id, bool accept)
    {
        var s = Need();
        to = Norm(to);
        if (!Online.TryGetValue(to, out var them)) return;
        await Clients.Client(them.ConnectionId).SendAsync("FileDecide", s.Username, id, accept);
    }

    public async Task FileChunk(string to, string id, int index, int total, string data)
    {
        var s = Need();
        to = Norm(to);
        if (!Online.TryGetValue(to, out var them)) return;
        await Clients.Client(them.ConnectionId).SendAsync("FileChunk", s.Username, id, index, total, data);
    }

    public async Task Whisper(string toUsername, string body)
    {
        var s = Need();
        toUsername = Norm(toUsername);
        if (!Online.TryGetValue(toUsername, out var them)) throw new HubException("They're offline.");
        if (Blocked(them.UserId, s.Username)) return;
        body = (body ?? "").Trim();
        if (body.Length == 0) return;
        var payload = new { from = s.Username, fromDisplay = s.Display, to = them.Username, body, kind = "whisper", at = DateTime.UtcNow };
        await Clients.Client(them.ConnectionId).SendAsync("Whisper", payload);
        await Clients.Caller.SendAsync("Whisper", payload);
    }

    public async Task Nudge(string? toUsername)
    {
        var s = Need();
        if (Online.TryGetValue(s.Username, out var self) && self.Invisible)
            throw new HubException("Nudge would give you away.");
        if (string.IsNullOrWhiteSpace(toUsername))
            await Clients.Others.SendAsync("Nudge", s.Display);
        else if (Online.TryGetValue(Norm(toUsername), out var them) && !Blocked(them.UserId, s.Username))
            await Clients.Client(them.ConnectionId).SendAsync("Nudge", s.Display);
    }

    public async Task Signal(string toUsername, string kind, string payload)
    {
        var s = Need();
        toUsername = Norm(toUsername);
        if (!Online.TryGetValue(toUsername, out var them)) return;
        if (Blocked(them.UserId, s.Username)) return;
        await Clients.Client(them.ConnectionId).SendAsync("Signal", s.Username, s.Display, kind, payload);
    }

    private Session Need() => (Session)(Context.Items["session"] ?? throw new HubException("No session"));
    private Session? Me() => Context.Items.TryGetValue("session", out var o) ? o as Session : null;

    private List<object> ListFriends(long userId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT f.friend_username, u.display, COALESCE(f.pending,0)
            FROM friends f LEFT JOIN users u ON u.username = f.friend_username
            WHERE f.user_id=$i
            """;
        cmd.Parameters.AddWithValue("$i", userId);
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
        {
            var uname = r.GetString(0);
            var display = r.IsDBNull(1) ? uname : r.GetString(1);
            var pending = r.GetInt64(2) != 0;
            var st = pending ? "awaiting"
                : Online.TryGetValue(uname, out var p) && !p.Invisible ? p.Status : "offline";
            list.Add(new { username = uname, display, status = st });
        }
        return list;
    }

    private List<object> ListAuth(string uin)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT from_uin, from_display FROM auth_req WHERE to_uin=$t";
        cmd.Parameters.AddWithValue("$t", uin);
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
            list.Add(new { from = r.GetString(0), display = r.GetString(1) });
        return list;
    }

    private List<object> TakeMail(string uin)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT from_uin, from_display, body, created FROM offline_mail WHERE to_uin=$t ORDER BY id";
        cmd.Parameters.AddWithValue("$t", uin);
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
            list.Add(new { from = r.GetString(0), display = r.GetString(1), body = r.GetString(2), at = r.GetString(3) });
        r.Close();
        using var del = c.CreateCommand();
        del.CommandText = "DELETE FROM offline_mail WHERE to_uin=$t";
        del.Parameters.AddWithValue("$t", uin);
        del.ExecuteNonQuery();
        return list;
    }

    private List<object> ListRooms()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT slug, title, COALESCE(NULLIF(channel,''), kind, 'hangout'),
                   COALESCE(topic,''), COALESCE(owner,''), COALESCE(welcome,''),
                   CASE WHEN pass_hash IS NOT NULL THEN 1 ELSE 0 END,
                   COALESCE(locked,0)
            FROM rooms ORDER BY id
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
        {
            var slug = r.GetString(0);
            var n = 0;
            var hot = false;
            if (Rooms.TryGetValue(slug, out var bag))
            {
                n = bag.Values.Count(x => !x.Ghost);
                hot = bag.Values.Any(x => x.Mic && !x.Ghost);
            }
            list.Add(new
            {
                slug,
                title = r.GetString(1),
                kind = r.GetString(2),
                channel = r.GetString(2),
                topic = r.IsDBNull(3) ? "" : r.GetString(3),
                owner = r.IsDBNull(4) ? "" : r.GetString(4),
                welcome = r.IsDBNull(5) ? "" : r.GetString(5),
                locked = r.GetInt64(6) != 0,
                doorLock = r.GetInt64(7) != 0,
                users = n,
                hot
            });
        }
        return list;
    }

    private RoomMeta? LoadRoom(string slug)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT slug, title, COALESCE(NULLIF(channel,''),'hangout'), COALESCE(owner,''),
                   COALESCE(topic,''), COALESCE(welcome,''), pass_salt, pass_hash,
                   COALESCE(locked,0), COALESCE(slow,0)
            FROM rooms WHERE slug=$s
            """;
        cmd.Parameters.AddWithValue("$s", slug);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new RoomMeta(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetString(5),
            r.IsDBNull(6) ? null : (byte[])r[6],
            r.IsDBNull(7) ? null : (byte[])r[7],
            r.GetInt64(8) != 0, r.GetInt64(9) != 0);
    }

    private string GetRole(string slug, string user, bool siteAdmin, string owner)
    {
        if (siteAdmin) return "owner";
        if (!string.IsNullOrEmpty(owner) && owner.Equals(user, StringComparison.OrdinalIgnoreCase)) return "owner";
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT role FROM room_roles WHERE slug=$s AND username=$u";
        cmd.Parameters.AddWithValue("$s", slug);
        cmd.Parameters.AddWithValue("$u", user);
        return cmd.ExecuteScalar() as string ?? "member";
    }

    private static int Rank(string role) => role switch
    {
        "owner" => 4,
        "admin" => 3,
        "mod" => 2,
        "muted" => 0,
        "banned" => -1,
        _ => 1
    };

    private static bool VerifyRoomPass(RoomMeta meta, string? password)
    {
        if (meta.PassSalt is null || meta.PassHash is null) return true;
        return Db.Verify(password ?? "", meta.PassSalt, meta.PassHash);
    }

    private static object InfoPayload(RoomMeta meta, string myRole, int users) => new
    {
        meta.Slug,
        meta.Title,
        meta.Channel,
        meta.Owner,
        meta.Topic,
        meta.Welcome,
        locked = meta.HasPass,
        doorLock = meta.DoorLock,
        slow = meta.Slow,
        myRole,
        users
    };

    private static string Slugify(string title)
    {
        var chars = title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : (c is ' ' or '-' ? '-' : '\0')).Where(c => c != '\0');
        var s = new string(chars.ToArray()).Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        if (s.Length < 2) s = "room";
        if (s.Length > 24) s = s[..24].Trim('-');
        return s;
    }

    private static string ChannelLabel(string ch) => ch switch
    {
        "music" => "Music",
        "tech" => "Tech",
        "gaming" => "Gaming",
        "dating" => "Dating",
        "afterdark" => "After Dark",
        "area850" => "Area 850",
        "war" => "War Room",
        _ => "Hangout"
    };

    private sealed record RoomMeta(string Slug, string Title, string Channel, string Owner, string Topic, string Welcome, byte[]? PassSalt, byte[]? PassHash, bool DoorLock, bool Slow)
    {
        public bool HasPass => PassHash is { Length: > 0 };
    }

    private List<object> History(string room, int take)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT user_id, display, body, kind, created FROM messages WHERE room=$r ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$r", room);
        cmd.Parameters.AddWithValue("$n", take);
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
        {
            list.Add(new
            {
                userId = r.GetInt64(0),
                display = r.GetString(1),
                body = r.GetString(2),
                kind = r.GetString(3),
                at = r.GetString(4)
            });
        }
        list.Reverse();
        return list;
    }

    private static string Norm(string u)
    {
        u = (u ?? "").Trim();
        if (u.Length == 0) return u;
        if (u.All(char.IsDigit))
        {
            var t = u.TrimStart('0');
            return t.Length == 0 ? "0" : t;
        }
        return u;
    }

    private bool Blocked(long userId, string from)
    {
        if (userId <= 0 || string.IsNullOrEmpty(from)) return false;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM blocks WHERE user_id=$i AND blocked=$u";
        cmd.Parameters.AddWithValue("$i", userId);
        cmd.Parameters.AddWithValue("$u", from);
        return cmd.ExecuteScalar() is not null;
    }

    private long UserIdOf(string username)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM users WHERE username=$u";
        cmd.Parameters.AddWithValue("$u", username);
        var v = cmd.ExecuteScalar();
        return v is long l ? l : v is int i ? i : 0;
    }

    private string StatusMsgOf(long userId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT status_msg FROM profiles WHERE user_id=$i";
        cmd.Parameters.AddWithValue("$i", userId);
        return cmd.ExecuteScalar() as string ?? "";
    }

    private static string? OtherInDm(string room, string me)
    {
        var parts = room[3..].Split(':');
        return parts.FirstOrDefault(x => !x.Equals(me, StringComparison.OrdinalIgnoreCase));
    }

    private static string Label(string status) => status switch
    {
        "away" => "Away",
        "occupied" => "Occupied",
        "dnd" => "Do not disturb",
        "invisible" => "Invisible",
        _ => "Online"
    };

    private readonly record struct Presence(long UserId, string Username, string Display, string Status, string ConnectionId, bool IsAdmin, bool Invisible);
    private readonly record struct Seat(string Username, string Display, string ConnectionId, bool Cam, bool Ghost, bool Mic);
}
