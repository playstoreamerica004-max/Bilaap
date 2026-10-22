using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BilapConnect;

// Server-side Twilio Verify integration. Credentials are read only from environment/configuration.
public class TwilioVerifyService(HttpClient http, IConfiguration config)
{
    private (string sid, string token, string service) GetSettings()
    {
        var sid = config["Twilio:AccountSid"];
        var token = config["Twilio:AuthToken"];
        var service = config["Twilio:VerifyServiceSid"];
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(service))
            throw new InvalidOperationException("إعدادات SMS غير مكتملة: عيّن Twilio__AccountSid وTwilio__AuthToken وTwilio__VerifyServiceSid.");
        return (sid, token, service);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, Dictionary<string, string> fields)
    {
        var (sid, token, _) = GetSettings();
        var req = new HttpRequestMessage(method, url) { Content = new FormUrlEncodedContent(fields) };
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sid}:{token}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        return req;
    }

    public async Task StartAsync(string phone)
    {
        var (sid, _, service) = GetSettings();
        var url = $"https://verify.twilio.com/v2/Services/{Uri.EscapeDataString(service)}/Verifications";
        using var req = CreateRequest(HttpMethod.Post, url, new() { ["To"] = phone, ["Channel"] = "sms" });
        using var response = await http.SendAsync(req);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"SMS provider returned {(int)response.StatusCode}");
    }

    public async Task<bool> CheckAsync(string phone, string code)
    {
        var (_, _, service) = GetSettings();
        var url = $"https://verify.twilio.com/v2/Services/{Uri.EscapeDataString(service)}/VerificationCheck";
        using var req = CreateRequest(HttpMethod.Post, url, new() { ["To"] = phone, ["Code"] = code });
        using var response = await http.SendAsync(req);
        if (!response.IsSuccessStatusCode) return false;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "approved";
    }
}

public static class Phone
{
    // Basic international format validation. For production, use libphonenumber-csharp.
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var value = new string(input.Trim().Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (!value.StartsWith('+') || value.Length < 9 || value.Length > 16 || value.Skip(1).Any(c => !char.IsDigit(c)))
            return null;
        return value;
    }
}
