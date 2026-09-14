using Area850.Server.Auth;
using Area850.Server.Data;
using Area850.Server.Hubs;

namespace Area850.Server;

public sealed class AdminOps
{
    private readonly Db _db;
    private readonly Sessions _sessions;

    public AdminOps(Db db, Sessions sessions)
    {
        _db = db;
        _sessions = sessions;
    }

    public object Overview()
    {
        using var c = _db.Open();
        long N(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
        }
        return new
        {
            users = N("SELECT COUNT(*) FROM users"),
            disabled = N("SELECT COUNT(*) FROM users WHERE COALESCE(disabled,0)=1"),
            messages = N("SELECT COUNT(*) FROM messages"),
            today = N("SELECT COUNT(*) FROM messages WHERE created >= strftime('%Y-%m-%d','now')"),
            rooms = N("SELECT COUNT(*) FROM rooms"),
            live = ChatHub.AdminLive()
        };
    }

    public List<object> Users(string? q)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT username, display, COALESCE(uin,0), COALESCE(is_admin,0), COALESCE(disabled,0), created
            FROM users
            WHERE ($q='' OR username LIKE $like OR display LIKE $like OR CAST(uin AS TEXT) LIKE $like)
            ORDER BY uin LIMIT 400
            """;
        var query = (q ?? "").Trim();
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$like", "%" + query + "%");
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
            list.Add(new
            {
                username = r.GetString(0),
                display = r.GetString(1),
                uin = r.GetInt64(2).ToString(),
                isAdmin = r.GetInt64(3) != 0,
                disabled = r.GetInt64(4) != 0,
                created = r.GetString(5)
            });
        return list;
    }

    public void SetPassword(string uin, string password)
    {
        password ??= "";
        if (password.Length < 4) throw new InvalidOperationException("Password at least 4 characters.");
        var (salt, hash) = Db.HashPassword(password);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET salt=$s, hash=$h WHERE username=$u OR CAST(uin AS TEXT)=$u";
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$u", Norm(uin));
        if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException("No such UIN.");
    }

    public async Task Disable(string uin, bool on, string actor, Func<string, string, Task>? notify)
    {
        var key = Norm(uin);
        if (key == "0" || key.Equals(actor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Can't disable the root admin.");
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET disabled=$d WHERE username=$u OR CAST(uin AS TEXT)=$u";
        cmd.Parameters.AddWithValue("$d", on ? 1 : 0);
        cmd.Parameters.AddWithValue("$u", key);
        if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException("No such UIN.");
        if (on) await Kick(key, "This number was disabled.", notify);
    }

    public async Task Kick(string username, string why, Func<string, string, Task>? notify)
    {
        var key = Norm(username);
        var conn = ChatHub.AdminDrop(key);
        _sessions.DropUser(key);
        if (conn is not null && notify is not null)
            await notify(conn, why);
    }

    public async Task DeleteUser(string uin, Func<string, string, Task>? notify)
    {
        var key = Norm(uin);
        if (key == "0") throw new InvalidOperationException("Can't delete UIN 0.");
        await Kick(key, "Account removed.", notify);
        using var c = _db.Open();
        void Run(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$u", key);
            cmd.ExecuteNonQuery();
        }
        Run("DELETE FROM friends WHERE friend_username=$u OR user_id=(SELECT id FROM users WHERE username=$u)");
        Run("DELETE FROM auth_req WHERE from_uin=$u OR to_uin=$u");
        Run("DELETE FROM blocks WHERE blocked=$u OR user_id=(SELECT id FROM users WHERE username=$u)");
        Run("DELETE FROM profiles WHERE user_id=(SELECT id FROM users WHERE username=$u)");
        Run("DELETE FROM room_roles WHERE username=$u");
        Run("DELETE FROM users WHERE username=$u");
    }

    public List<object> Rooms()
    {
        var occ = ChatHub.Occupancy();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT slug, title, COALESCE(NULLIF(channel,''),'hangout'), COALESCE(owner,''),
                   COALESCE(topic,''), CASE WHEN pass_hash IS NOT NULL THEN 1 ELSE 0 END,
                   COALESCE(locked,0), COALESCE(slow,0),
                   (SELECT COUNT(*) FROM messages m WHERE m.room=r.slug)
            FROM rooms r ORDER BY title
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
        {
            var slug = r.GetString(0);
            occ.TryGetValue(slug, out var n);
            list.Add(new
            {
                slug,
                title = r.GetString(1),
                channel = r.GetString(2),
                owner = r.GetString(3),
                topic = r.GetString(4),
                locked = r.GetInt64(5) != 0,
                doorLock = r.GetInt64(6) != 0,
                slow = r.GetInt64(7) != 0,
                messages = r.GetInt64(8),
                users = n
            });
        }
        return list;
    }

    public void LockRoom(string slug, bool on)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE rooms SET locked=$l WHERE slug=$s";
        cmd.Parameters.AddWithValue("$l", on ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", slug);
        cmd.ExecuteNonQuery();
    }

    public void ClearPass(string slug)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE rooms SET pass_salt=NULL, pass_hash=NULL WHERE slug=$s";
        cmd.Parameters.AddWithValue("$s", slug);
        cmd.ExecuteNonQuery();
    }

    public void DeleteRoom(string slug)
    {
        using var c = _db.Open();
        void Run(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$s", slug);
            cmd.ExecuteNonQuery();
        }
        Run("DELETE FROM room_roles WHERE slug=$s");
        Run("DELETE FROM messages WHERE room=$s");
        Run("DELETE FROM rooms WHERE slug=$s");
    }

    public List<object> Messages(string? room, int take)
    {
        take = Math.Clamp(take, 1, 200);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        if (string.IsNullOrWhiteSpace(room))
            cmd.CommandText = "SELECT display, room, body, created FROM messages ORDER BY id DESC LIMIT $n";
        else
        {
            cmd.CommandText = "SELECT display, room, body, created FROM messages WHERE room=$r ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$r", room);
        }
        cmd.Parameters.AddWithValue("$n", take);
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
            list.Add(new { display = r.GetString(0), room = r.GetString(1), body = r.GetString(2), at = r.GetString(3) });
        return list;
    }

    private static string Norm(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0 || !s.All(char.IsDigit)) return s;
        var t = s.TrimStart('0');
        return t.Length == 0 ? "0" : t;
    }
}
