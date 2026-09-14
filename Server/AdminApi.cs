using Area850.Server.Auth;
using Area850.Server.Data;
using Area850.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Area850.Server;

public static class AdminApi
{
    public static void MapAdmin(this WebApplication app)
    {
        app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));
        app.MapGet("/admin/", () => Results.Redirect("/admin/index.html"));

        app.MapPost("/api/admin/login", (HttpContext ctx, Db db, Sessions sessions, AuthReq req) =>
        {
            var key = Norm(req.Username ?? req.Uin ?? "");
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, username, display, salt, hash, COALESCE(is_admin,0), COALESCE(disabled,0) FROM users WHERE username=$u OR CAST(uin AS TEXT)=$u";
            cmd.Parameters.AddWithValue("$u", key);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return Results.Json(new { error = "Unknown number." }, statusCode: 401);
            var id = r.GetInt64(0);
            var uname = r.GetString(1);
            var display = r.GetString(2);
            var salt = (byte[])r["salt"];
            var hash = (byte[])r["hash"];
            var isAdmin = r.GetInt64(5) != 0;
            var disabled = r.GetInt64(6) != 0;
            if (disabled) return Results.Json(new { error = "Disabled." }, statusCode: 403);
            if (!isAdmin) return Results.Json(new { error = "Admin only." }, statusCode: 403);
            if (!Db.Verify(req.Password ?? "", salt, hash)) return Results.Json(new { error = "Wrong password." }, statusCode: 401);
            var sess = sessions.Issue(id, uname, display, true, false);
            ctx.Response.Cookies.Append("850_admin", sess.Token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromDays(7)
            });
            return Results.Ok(new { ok = true, display, username = uname });
        });

        app.MapPost("/api/admin/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete("850_admin", new CookieOptions { Path = "/" });
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/admin/overview", (HttpContext ctx, Db db, Sessions sessions) =>
        {
            var s = Need(ctx, sessions);
            if (s is null) return Results.Unauthorized();
            using var c = db.Open();
            long N(string sql)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
            }
            var live = ChatHub.AdminLive();
            return Results.Ok(new
            {
                me = s.Display,
                users = N("SELECT COUNT(*) FROM users"),
                disabled = N("SELECT COUNT(*) FROM users WHERE COALESCE(disabled,0)=1"),
                messages = N("SELECT COUNT(*) FROM messages"),
                today = N("SELECT COUNT(*) FROM messages WHERE created >= strftime('%Y-%m-%d','now')"),
                rooms = N("SELECT COUNT(*) FROM rooms"),
                live
            });
        });

        app.MapGet("/api/admin/users", (HttpContext ctx, Db db, Sessions sessions, string? q) =>
        {
            if (Need(ctx, sessions) is null) return Results.Unauthorized();
            using var c = db.Open();
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
            {
                var uname = r.GetString(0);
                list.Add(new
                {
                    username = uname,
                    display = r.GetString(1),
                    uin = r.GetInt64(2).ToString(),
                    isAdmin = r.GetInt64(3) != 0,
                    disabled = r.GetInt64(4) != 0,
                    created = r.GetString(5)
                });
            }
            return Results.Ok(list);
        });

        app.MapPost("/api/admin/users/{uin}/password", (HttpContext ctx, Db db, Sessions sessions, string uin, PwReq req) =>
        {
            if (Need(ctx, sessions) is null) return Results.Unauthorized();
            var pass = req.Password ?? "";
            if (pass.Length < 4) return Results.BadRequest("Password at least 4 characters.");
            var (salt, hash) = Db.HashPassword(pass);
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE users SET salt=$s, hash=$h WHERE username=$u OR CAST(uin AS TEXT)=$u";
            cmd.Parameters.AddWithValue("$s", salt);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$u", Norm(uin));
            if (cmd.ExecuteNonQuery() == 0) return Results.NotFound();
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/admin/users/{uin}/disable", async (HttpContext ctx, Db db, Sessions sessions, IHubContext<ChatHub> hub, string uin, FlagReq req) =>
        {
            var admin = Need(ctx, sessions);
            if (admin is null) return Results.Unauthorized();
            var key = Norm(uin);
            if (key == "0" || key.Equals(admin.Username, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Can't disable the root admin.");
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE users SET disabled=$d WHERE username=$u OR CAST(uin AS TEXT)=$u";
            cmd.Parameters.AddWithValue("$d", req.On ? 1 : 0);
            cmd.Parameters.AddWithValue("$u", key);
            if (cmd.ExecuteNonQuery() == 0) return Results.NotFound();
            if (req.On) await Kick(sessions, hub, key, "This number was disabled.");
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/admin/users/{uin}/kick", async (HttpContext ctx, Sessions sessions, IHubContext<ChatHub> hub, string uin) =>
        {
            if (Need(ctx, sessions) is null) return Results.Unauthorized();
            await Kick(sessions, hub, Norm(uin), "Signed out by admin.");
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/api/admin/users/{uin}", async (HttpContext ctx, Db db, Sessions sessions, IHubContext<ChatHub> hub, string uin) =>
        {
            var admin = Need(ctx, sessions);
            if (admin is null) return Results.Unauthorized();
            var key = Norm(uin);
            if (key == "0") return Results.BadRequest("Can't delete UIN 0.");
            await Kick(sessions, hub, key, "Account removed.");
            using var c = db.Open();
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
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/admin/rooms", (HttpContext ctx, Db db, Sessions sessions) =>
        {
            if (Need(ctx, sessions) is null) return Results.Unauthorized();
            var live = ChatHub.AdminLive();
            var occ = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var people = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (live is not null)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(live);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("occupancy", out var arr))
                {
                    foreach (var o in arr.EnumerateArray())
                    {
                        var slug = o.GetProperty("slug").GetString() ?? "";
                        occ[slug] = o.GetProperty("n").GetInt32();
                        people[slug] = o.GetProperty("people");
                    }
                }
            }
            using var c = db.Open();
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
                    users = occ.GetValueOrDefault(slug),
                    people = people.GetValueOrDefault(slug)
                });
            }
            return Results.Ok(list);
        });

        app.MapPost("/api/admin/rooms/{slug}/lock", (HttpContext ctx, Db db, Sessions sessions, string slug, FlagReq req) =>
        {
            if (Need(ctx, sessions) is null) return Results.Unauthorized();
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE rooms SET locked=$l WHERE slug=$s";
            cmd.Parameters.AddWithValue("$l", req.On ? 1 : 0);
            cmd.Parameters.AddWithValue("$s", slug);
            cmd.ExecuteNonQuery();
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/admin/rooms/{slug}/clear-pass", (HttpContext ctx, Db db, Sessions sessions, string slug) =>
        {
            if (Need(ctx, sessions) is null) return Results.Unauthorized();
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE rooms SET pass_salt=NULL, pass_hash=NULL WHERE slug=$s";
            cmd.Parameters.AddWithValue("$s", slug);
            cmd.ExecuteNonQuery();
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/admin/rooms/{slug}/say", async (HttpContext ctx, Sessions sessions, IHubContext<ChatHub> hub, string slug, SayReq req) =>
        {
            if (Need(ctx, sessions) is null) return Results.Unauthorized();
            var body = (req.Body ?? "").Trim();
            if (body.Length == 0) return Results.BadRequest("Empty.");
            await hub.Clients.Group("room:" + slug).SendAsync("System", slug, "ADMIN: " + body);
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/api/admin/rooms/{slug}", async (HttpContext ctx, Db db, Sessions sessions, IHubContext<ChatHub> hub, string slug) =>
        {
            if (Need(ctx, sessions) is null) return Results.Unauthorized();
            await hub.Clients.Group("room:" + slug).SendAsync("Kicked", slug, "Room closed by admin.");
            using var c = db.Open();
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
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/admin/messages", (HttpContext ctx, Db db, Sessions sessions, string? room, int take = 80) =>
        {
            if (Need(ctx, sessions) is null) return Results.Unauthorized();
            take = Math.Clamp(take, 1, 200);
            using var c = db.Open();
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
            return Results.Ok(list);
        });
    }

    private static Session? Need(HttpContext ctx, Sessions sessions)
    {
        ctx.Request.Cookies.TryGetValue("850_admin", out var token);
        var s = sessions.Get(token);
        return s is { IsAdmin: true } ? s : null;
    }

    private static async Task Kick(Sessions sessions, IHubContext<ChatHub> hub, string username, string why)
    {
        var conn = ChatHub.AdminDrop(username);
        sessions.DropUser(username);
        if (conn is not null)
            await hub.Clients.Client(conn).SendAsync("Kicked", "", why);
    }

    private static string Norm(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0 || !s.All(char.IsDigit)) return s;
        var t = s.TrimStart('0');
        return t.Length == 0 ? "0" : t;
    }

    public record PwReq(string? Password);
    public record FlagReq(bool On);
    public record SayReq(string? Body);
}
