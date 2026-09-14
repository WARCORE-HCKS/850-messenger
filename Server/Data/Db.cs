using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Area850.Server.Data;

public sealed class Db
{
    private readonly string _cs;
    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Init();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              username TEXT NOT NULL UNIQUE COLLATE NOCASE,
              display TEXT NOT NULL,
              salt BLOB NOT NULL,
              hash BLOB NOT NULL,
              created TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rooms (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              slug TEXT NOT NULL UNIQUE,
              title TEXT NOT NULL,
              kind TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              room TEXT NOT NULL,
              user_id INTEGER NOT NULL,
              display TEXT NOT NULL,
              body TEXT NOT NULL,
              kind TEXT NOT NULL,
              created TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS friends (
              user_id INTEGER NOT NULL,
              friend_username TEXT NOT NULL COLLATE NOCASE,
              PRIMARY KEY (user_id, friend_username)
            );
            CREATE TABLE IF NOT EXISTS auth_req (
              from_uin TEXT NOT NULL,
              from_display TEXT NOT NULL,
              to_uin TEXT NOT NULL,
              note TEXT,
              created TEXT NOT NULL,
              PRIMARY KEY (from_uin, to_uin)
            );
            CREATE TABLE IF NOT EXISTS blocks (
              user_id INTEGER NOT NULL,
              blocked TEXT NOT NULL COLLATE NOCASE,
              PRIMARY KEY (user_id, blocked)
            );
            CREATE TABLE IF NOT EXISTS profiles (
              user_id INTEGER PRIMARY KEY,
              about TEXT,
              status_msg TEXT
            );
            CREATE TABLE IF NOT EXISTS offline_mail (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              to_uin TEXT NOT NULL,
              from_uin TEXT NOT NULL,
              from_display TEXT NOT NULL,
              body TEXT NOT NULL,
              created TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        try { using var a = c.CreateCommand(); a.CommandText = "ALTER TABLE users ADD COLUMN is_admin INTEGER NOT NULL DEFAULT 0"; a.ExecuteNonQuery(); } catch { }
        try { using var a = c.CreateCommand(); a.CommandText = "ALTER TABLE users ADD COLUMN uin INTEGER"; a.ExecuteNonQuery(); } catch { }
        try { using var a = c.CreateCommand(); a.CommandText = "ALTER TABLE friends ADD COLUMN pending INTEGER NOT NULL DEFAULT 0"; a.ExecuteNonQuery(); } catch { }
        try { using var a = c.CreateCommand(); a.CommandText = "ALTER TABLE profiles ADD COLUMN city TEXT"; a.ExecuteNonQuery(); } catch { }
        try { using var a = c.CreateCommand(); a.CommandText = "ALTER TABLE profiles ADD COLUMN gender TEXT"; a.ExecuteNonQuery(); } catch { }
        try { using var a = c.CreateCommand(); a.CommandText = "ALTER TABLE users ADD COLUMN disabled INTEGER NOT NULL DEFAULT 0"; a.ExecuteNonQuery(); } catch { }
        foreach (var sql in new[]
        {
            "ALTER TABLE rooms ADD COLUMN channel TEXT NOT NULL DEFAULT 'hangout'",
            "ALTER TABLE rooms ADD COLUMN owner TEXT",
            "ALTER TABLE rooms ADD COLUMN topic TEXT",
            "ALTER TABLE rooms ADD COLUMN welcome TEXT",
            "ALTER TABLE rooms ADD COLUMN pass_salt BLOB",
            "ALTER TABLE rooms ADD COLUMN pass_hash BLOB",
            "ALTER TABLE rooms ADD COLUMN locked INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE rooms ADD COLUMN slow INTEGER NOT NULL DEFAULT 0"
        })
        {
            try { using var a = c.CreateCommand(); a.CommandText = sql; a.ExecuteNonQuery(); } catch { }
        }
        using (var roles = c.CreateCommand())
        {
            roles.CommandText = """
                CREATE TABLE IF NOT EXISTS room_roles (
                  slug TEXT NOT NULL,
                  username TEXT NOT NULL COLLATE NOCASE,
                  role TEXT NOT NULL,
                  PRIMARY KEY (slug, username)
                );
                """;
            roles.ExecuteNonQuery();
        }
        SeedRooms(c);
        SeedMoreRooms(c);
        SeedAdmin(c);
        BackfillUins(c);
    }

    public static long NextUin(SqliteConnection c)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COALESCE(MAX(uin), 99999) FROM users WHERE uin >= 100000";
        var max = Convert.ToInt64(q.ExecuteScalar() ?? 99999L);
        return Math.Max(100000, max + 1);
    }

    private static void BackfillUins(SqliteConnection c)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT id FROM users WHERE uin IS NULL ORDER BY id";
        using var r = q.ExecuteReader();
        var ids = new List<long>();
        while (r.Read()) ids.Add(r.GetInt64(0));
        r.Close();
        foreach (var id in ids)
        {
            var uin = NextUin(c);
            using var up = c.CreateCommand();
            up.CommandText = "UPDATE users SET uin=$n, username=$s WHERE id=$i";
            up.Parameters.AddWithValue("$n", uin);
            up.Parameters.AddWithValue("$s", uin.ToString());
            up.Parameters.AddWithValue("$i", id);
            up.ExecuteNonQuery();
        }
    }

    private static void SeedAdmin(SqliteConnection c)
    {
        const string display = "WARCORE";
        var (salt, hash) = HashPassword("area850");
        using var find = c.CreateCommand();
        find.CommandText = "SELECT id FROM users WHERE uin=0 OR uin=850 OR username IN ('0','850','warcore')";
        var id = find.ExecuteScalar();
        if (id is null)
        {
            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT INTO users(username,display,salt,hash,created,is_admin,uin) VALUES('0',$d,$s,$h,$c,1,0)";
            ins.Parameters.AddWithValue("$d", display);
            ins.Parameters.AddWithValue("$s", salt);
            ins.Parameters.AddWithValue("$h", hash);
            ins.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            ins.ExecuteNonQuery();
        }
        else
        {
            using var up = c.CreateCommand();
            up.CommandText = "UPDATE users SET username='0', display=$d, salt=$s, hash=$h, is_admin=1, uin=0 WHERE id=$i";
            up.Parameters.AddWithValue("$d", display);
            up.Parameters.AddWithValue("$s", salt);
            up.Parameters.AddWithValue("$h", hash);
            up.Parameters.AddWithValue("$i", id);
            up.ExecuteNonQuery();
        }
    }

    private static void SeedRooms(SqliteConnection c)
    {
        using var check = c.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM rooms";
        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;
        foreach (var (slug, title, channel, topic, welcome) in StarterRooms())
        {
            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT INTO rooms(slug,title,kind,channel,owner,topic,welcome) VALUES($s,$t,'public',$ch,'0',$p,$w)";
            ins.Parameters.AddWithValue("$s", slug);
            ins.Parameters.AddWithValue("$t", title);
            ins.Parameters.AddWithValue("$ch", channel);
            ins.Parameters.AddWithValue("$p", topic);
            ins.Parameters.AddWithValue("$w", welcome);
            ins.ExecuteNonQuery();
            using var role = c.CreateCommand();
            role.CommandText = "INSERT OR IGNORE INTO room_roles(slug,username,role) VALUES($s,'0','owner')";
            role.Parameters.AddWithValue("$s", slug);
            role.ExecuteNonQuery();
        }
    }

    private static void SeedMoreRooms(SqliteConnection c)
    {
        using var ch = c.CreateCommand();
        ch.CommandText = "UPDATE rooms SET channel='hangout', owner=COALESCE(owner,'0') WHERE channel IS NULL OR channel='' OR channel='public'";
        ch.ExecuteNonQuery();
        using var a850 = c.CreateCommand();
        a850.CommandText = "UPDATE rooms SET channel='area850' WHERE slug='area850'";
        a850.ExecuteNonQuery();
        using var war = c.CreateCommand();
        war.CommandText = "UPDATE rooms SET channel='war' WHERE slug='warroom'";
        war.ExecuteNonQuery();
        using var night = c.CreateCommand();
        night.CommandText = "UPDATE rooms SET channel='afterdark' WHERE slug='nightwatch'";
        night.ExecuteNonQuery();
        foreach (var (slug, title, channel, topic, welcome) in StarterRooms())
        {
            using var ins = c.CreateCommand();
            ins.CommandText = """
                INSERT OR IGNORE INTO rooms(slug,title,kind,channel,owner,topic,welcome)
                VALUES($s,$t,'public',$ch,'0',$p,$w)
                """;
            ins.Parameters.AddWithValue("$s", slug);
            ins.Parameters.AddWithValue("$t", title);
            ins.Parameters.AddWithValue("$ch", channel);
            ins.Parameters.AddWithValue("$p", topic);
            ins.Parameters.AddWithValue("$w", welcome);
            ins.ExecuteNonQuery();
            using var role = c.CreateCommand();
            role.CommandText = "INSERT OR IGNORE INTO room_roles(slug,username,role) VALUES($s,'0','owner')";
            role.Parameters.AddWithValue("$s", slug);
            role.ExecuteNonQuery();
        }
    }

    private static (string slug, string title, string channel, string topic, string welcome)[] StarterRooms() =>
    [
        ("lobby", "Lobby", "hangout", "Everyone starts here.", "Welcome to the Lobby. Hold Ctrl to talk. Be decent."),
        ("area850", "Area 850", "area850", "Home turf.", "You found Area 850. Don't make it weird."),
        ("warroom", "War Room", "war", "Strategy, heat, no small talk.", "War Room. Mods have the floor."),
        ("nightwatch", "Night Watch", "afterdark", "After hours.", "Night Watch. Lights low, keep it in the room."),
        ("offcomms", "Off-Comms", "hangout", "Side channel.", "Off-Comms. The hallway behind the hallway."),
        ("latebeats", "Late Night Beats", "music", "Drop a track, not a lecture.", "Music room. Cam optional. Taste required."),
        ("wireheads", "Wireheads", "tech", "Build, break, brag.", "Tech. If it compiles, it ships."),
        ("arcade", "Arcade", "gaming", "Scores and sore losers.", "Arcade. GG or kick."),
        ("firstlook", "First Look", "dating", "Say hi. Don't be a creep.", "Dating. Consent is the password.")
    ];

    public static (byte[] salt, byte[] hash) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 80_000, HashAlgorithmName.SHA256, 32);
        return (salt, hash);
    }

    public static bool Verify(string password, byte[] salt, byte[] hash)
    {
        var test = Rfc2898DeriveBytes.Pbkdf2(password, salt, 80_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(test, hash);
    }
}
