using Area850.Server;
using Area850.Server.Auth;
using Area850.Server.Data;
using Area850.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Microsoft.AspNetCore.HttpOverrides;
using System.Windows.Forms;

using var mutex = new Mutex(true, @"Global\Area850.Server.host", out var owned);
using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\Area850.Server.show");
if (!owned)
{
    wake.Set();
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:8500");
builder.WebHost.UseWebRoot(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});
var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Area850");
builder.Services.AddSingleton(new Db(Path.Combine(dataDir, "850.db")));
builder.Services.AddSingleton<Sessions>();
builder.Services.AddSingleton<AdminOps>();
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 96 * 1024);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials()));

var app = builder.Build();
app.UseForwardedHeaders();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/info", () => Results.Json(new
{
    product = "850 Messenger",
    publisher = "WARCORE",
    site = "https://area850.com",
    lan = LanIp()
}));

app.MapPost("/api/register", (Db db, Sessions sessions, AuthReq req) =>
{
    var pass = req.Password ?? "";
    var display = (req.Display ?? "").Trim();
    if (display.Length < 2 || display.Length > 24) return Results.BadRequest("Nickname 2–24 characters.");
    if (pass.Length < 4) return Results.BadRequest("Password at least 4 characters.");
    var (salt, hash) = Db.HashPassword(pass);
    try
    {
        using var c = db.Open();
        var uin = Db.NextUin(c);
        var uinStr = uin.ToString();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO users(username,display,salt,hash,created,is_admin,uin) VALUES($u,$d,$s,$h,$c,0,$n)";
        cmd.Parameters.AddWithValue("$u", uinStr);
        cmd.Parameters.AddWithValue("$d", display);
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$n", uin);
        cmd.ExecuteNonQuery();
        using var idc = c.CreateCommand();
        idc.CommandText = "SELECT last_insert_rowid()";
        var id = (long)(idc.ExecuteScalar() ?? 0L);
        var sess = sessions.Issue(id, uinStr, display, false, false);
        return Results.Ok(new { token = sess.Token, userId = id, username = uinStr, uin, display, isAdmin = false });
    }
    catch (SqliteException)
    {
        return Results.Conflict("Could not create a number. Try again.");
    }
});

app.MapPost("/api/login", (Db db, Sessions sessions, AuthReq req) =>
{
    var key = NormalizeUin(req.Username ?? req.Uin ?? "");
    using var c = db.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT id, username, display, salt, hash, COALESCE(is_admin,0), uin, COALESCE(disabled,0) FROM users WHERE username=$u OR CAST(uin AS TEXT)=$u";
    cmd.Parameters.AddWithValue("$u", key);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.Json(new { error = "Unknown number." }, statusCode: 401);
    var id = r.GetInt64(0);
    var uname = r.GetString(1);
    var display = r.GetString(2);
    var salt = (byte[])r["salt"];
    var hash = (byte[])r["hash"];
    var isAdmin = r.GetInt64(5) != 0;
    var uin = r.IsDBNull(6) ? 0L : r.GetInt64(6);
    var disabled = r.GetInt64(7) != 0;
    if (disabled) return Results.Json(new { error = "This number is disabled." }, statusCode: 403);
    if (!Db.Verify(req.Password ?? "", salt, hash)) return Results.Json(new { error = "Wrong password." }, statusCode: 401);
    var ghost = req.Invisible;
    var sess = sessions.Issue(id, uname, display, isAdmin, ghost);
    return Results.Ok(new { token = sess.Token, userId = id, username = uname, uin, display, isAdmin, invisible = ghost });
});

app.MapAdmin();
app.MapHub<ChatHub>("/hub");

var listen = "http://127.0.0.1:8500";
var lan = LanIp();
ServerForm? form = null;
var uiReady = new ManualResetEventSlim(false);
var ui = new Thread(() =>
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    form = new ServerForm(
        listen,
        lan,
        () =>
        {
            try { app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication(); }
            catch { Environment.Exit(0); }
        },
        app.Services.GetRequiredService<AdminOps>(),
        app.Services.GetRequiredService<IHubContext<ChatHub>>());
    uiReady.Set();
    new Thread(() =>
    {
        while (true)
        {
            wake.WaitOne();
            try { form?.BeginInvoke(form.Reveal); } catch { }
        }
    }) { IsBackground = true }.Start();
    Application.Run(form);
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
uiReady.Wait();

app.Run();
try { Application.Exit(); } catch { }

static string LanIp()
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        var tag = ni.Name + ni.Description;
        if (tag.Contains("virtual", StringComparison.OrdinalIgnoreCase)) continue;
        if (tag.Contains("vmware", StringComparison.OrdinalIgnoreCase)) continue;
        if (tag.Contains("vbox", StringComparison.OrdinalIgnoreCase)) continue;
        if (tag.Contains("loopback", StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
        {
            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (IPAddress.IsLoopback(ua.Address)) continue;
            var s = ua.Address.ToString();
            if (s.StartsWith("169.254.")) continue;
            return s;
        }
    }
    return "127.0.0.1";
}

static string NormalizeUin(string s)
{
    s = (s ?? "").Trim();
    if (s.Length == 0) return s;
    if (!s.All(char.IsDigit)) return s;
    var t = s.TrimStart('0');
    return t.Length == 0 ? "0" : t;
}

record AuthReq(string? Username, string? Password, string? Display, string? Uin = null, bool Invisible = false);
