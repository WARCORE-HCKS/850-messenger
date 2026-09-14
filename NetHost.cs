using Area850.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Area850;

internal sealed class NetHost : IAsyncDisposable
{
    private WebApplication? _app;
    public string Url { get; private set; } = "http://127.0.0.1:8500";
    public string LanUrl { get; private set; } = "";

    public async Task StartAsync()
    {
        var root = AppContext.BaseDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            WebRootPath = Path.Combine(root, "wwwroot")
        });
        builder.WebHost.UseUrls("http://0.0.0.0:8500");
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<Roster>();
        _app = builder.Build();
        _app.UseDefaultFiles();
        _app.UseStaticFiles();
        _app.MapHub<NetHub>("/hub");
        _app.MapGet("/api/info", () =>
        {
            var lan = LanAddress();
            return Results.Json(new
            {
                product = "850 NET",
                publisher = "WARCORE",
                site = "https://area850.com",
                lan,
                local = "http://127.0.0.1:8500"
            });
        });
        await _app.StartAsync();
        Url = "http://127.0.0.1:8500";
        var ip = LanAddress();
        LanUrl = string.IsNullOrEmpty(ip) ? Url : $"http://{ip}:8500";
    }

    public static string LanAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
                var tag = ni.Name + " " + ni.Description;
                if (tag.Contains("virtual", StringComparison.OrdinalIgnoreCase)) continue;
                if (tag.Contains("vmware", StringComparison.OrdinalIgnoreCase)) continue;
                if (tag.Contains("vbox", StringComparison.OrdinalIgnoreCase)) continue;
                if (tag.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)) continue;
                if (tag.Contains("loopback", StringComparison.OrdinalIgnoreCase)) continue;
                if (tag.Contains("bluetooth", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.")) continue;
                    return s;
                }
            }
        }
        catch { }
        return "";
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
