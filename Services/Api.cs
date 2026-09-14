using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Area850.Services;

public sealed class Api
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public string Base { get; set; } = "http://127.0.0.1:8500";

    public async Task<(bool ok, string error, AuthOk? data)> Register(string password, string display)
    {
        try
        {
            var res = await _http.PostAsJsonAsync(Base + "/api/register", new { password, display });
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (false, Unwrap(body), null);
            return (true, "", JsonSerializer.Deserialize<AuthOk>(body, Json));
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    public async Task<(bool ok, string error, AuthOk? data)> Login(string username, string password, bool invisible = false)
    {
        try
        {
            var res = await _http.PostAsJsonAsync(Base + "/api/login", new { username, uin = username, password, invisible });
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (false, Unwrap(body), null);
            return (true, "", JsonSerializer.Deserialize<AuthOk>(body, Json));
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    private static string Unwrap(string body)
    {
        body = body.Trim().Trim('"');
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? body;
        }
        catch { }
        return string.IsNullOrWhiteSpace(body) ? "Request failed." : body;
    }
}

public sealed class AuthOk
{
    public string Token { get; set; } = "";
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public long Uin { get; set; }
    public string Display { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool Invisible { get; set; }
}
