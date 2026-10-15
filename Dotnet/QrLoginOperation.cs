using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VRCX;

// Owns an isolated cookie jar until the frontend activates the verified account.
// No provider token, device code or cookie value is returned by Snapshot().
internal sealed class QrLoginOperation : IDisposable
{
    internal static readonly Uri Api = new("https://api.vrchat.cloud/api/1/");
    internal static readonly string[] CookieNames = { "auth", "twoFactorAuth" };
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromMinutes(5));
    private readonly HttpClient _client;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private string _status = "waiting";
    private string _error;
    private JsonElement? _user;
    private string _verificationUrl;
    private string _stage = "authorization";
    private int? _httpStatus;
    private string[] _responseFields = Array.Empty<string>();
    private bool _disposed;
    internal readonly string Id = Guid.NewGuid().ToString("N");
    internal readonly CookieContainer Cookies = new();
    internal DateTimeOffset ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

    internal QrLoginOperation(IWebProxy proxy, string userAgent,
        HttpMessageHandler transport = null, Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = Cookies,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false
        };
        if (proxy != null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        if (transport != null) handler.Dispose();
        _client = new HttpClient(transport ?? handler) { BaseAddress = Api, Timeout = TimeSpan.FromSeconds(30) };
        _delay = delay ?? Task.Delay;
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    internal void Start() => _ = RunDeviceAsync();

    internal string Snapshot()
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow >= ExpiresAt && _status is "waiting" or "validating" or "ready")
            {
                _status = "expired";
                _stop.Cancel();
            }
            object summary = null;
            if (_user.HasValue)
            {
                var user = _user.Value;
                summary = new { id = Text(user, "id"), displayName = Text(user, "displayName") };
            }
            return JsonSerializer.Serialize(new
            {
                operationId = Id,
                status = _status,
                error = _error,
                user = summary,
                verificationUrl = _status == "waiting" ? _verificationUrl : null,
                expiresAt = ExpiresAt.ToUnixTimeMilliseconds(),
                stage = _stage,
                httpStatus = _httpStatus,
                responseFields = _status == "failed" ? _responseFields : Array.Empty<string>()
            });
        }
    }

    private void Fail(string code)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _status = "failed";
            _error = code;
            _stop.Cancel();
        }
    }

    internal static string Text(JsonElement json, string key) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void SetStage(string stage)
    {
        lock (_gate) { _stage = stage; _httpStatus = null; _responseFields = Array.Empty<string>(); }
    }

    private async Task<(int Status, JsonElement Json)> SendAsync(string path, object body = null)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, path);
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, _stop.Token);
        lock (_gate) _httpStatus = (int)response.StatusCode;
        var bytes = await response.Content.ReadAsByteArrayAsync(_stop.Token);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException();
        using var document = JsonDocument.Parse(bytes);
        lock (_gate) _responseFields = document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().Select(p => p.Name).Where(n => Regex.IsMatch(n, @"^[A-Za-z][A-Za-z0-9_]{0,39}$")).Take(16).ToArray()
            : Array.Empty<string>();
        return ((int)response.StatusCode, document.RootElement.Clone());
    }

    private async Task ValidateAsync(string expectedUserId = null)
    {
        SetStage("verify_account");
        var response = await SendAsync("auth/user");
        var id = Text(response.Json, "id");
        if (response.Status != 200 || id == null || !id.StartsWith("usr_", StringComparison.Ordinal))
        {
            Fail(response.Status == 429 ? "rate_limited" : "website_required");
            return;
        }
        if (expectedUserId != null && expectedUserId != id)
        {
            Fail("account_mismatch");
            return;
        }
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _user = response.Json;
            _status = "ready";
        }
    }

    internal (CookieContainer Cookies, JsonElement User) TakeSession()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || DateTimeOffset.UtcNow >= ExpiresAt || _status != "ready" || !_user.HasValue)
                throw new InvalidOperationException("Login is not ready.");
            _status = "completed";
            _stop.Cancel();
            return (Cookies, _user.Value);
        }
    }

    internal static void ImportCookies(CookieContainer jar, JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 2)
            throw new InvalidDataException();
        var seen = new HashSet<string>();
        var imported = new List<Cookie>();
        foreach (var value in values.EnumerateArray())
        {
            var name = Text(value, "name");
            var token = Text(value, "value");
            if (!CookieNames.Contains(name) || !seen.Add(name) || string.IsNullOrEmpty(token) || token.Length > 4096 ||
                token.Any(c => c < 0x21 || c > 0x7e || c is ';' or ',' or '"' or '\\'))
                throw new InvalidDataException();
            var cookie = new Cookie(name, token, "/", Api.Host) { Secure = true, HttpOnly = true };
            if (value.TryGetProperty("expires", out var expires) && expires.TryGetInt64(out var seconds) && seconds > 0)
            {
                cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                if (cookie.Expired) throw new InvalidDataException();
            }
            imported.Add(cookie);
        }
        if (!seen.Contains("auth")) throw new InvalidDataException();
        foreach (var cookie in imported) jar.Add(cookie);
    }

    internal async Task RestoreAsync(string json, string expectedUserId)
    {
        using var doc = JsonDocument.Parse(json);
        ImportCookies(Cookies, doc.RootElement);
        await ValidateAsync(expectedUserId);
    }

    private async Task RunDeviceAsync()
    {
        try
        {
            var result = await SendAsync("auth/device/code", new { });
            if (result.Status != 200) { Fail(result.Status == 429 ? "rate_limited" : "unavailable"); return; }
            var code = Text(result.Json, "deviceCode");
            var url = Text(result.Json, "verificationUrlComplete");
            if (code == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.Host != "vrchat.com" || uri.AbsolutePath != "/home/device") { Fail("unavailable"); return; }
            var interval = result.Json.TryGetProperty("interval", out var value) && value.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 5, 60) : 5;
            var expires = result.Json.TryGetProperty("expiresIn", out value) && value.TryGetInt32(out seconds) ? Math.Clamp(seconds, 1, 300) : 300;
            lock (_gate) { _verificationUrl = url; ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires); }
            while (!_stop.IsCancellationRequested && DateTimeOffset.UtcNow < ExpiresAt)
            {
                await _delay(TimeSpan.FromSeconds(interval), _stop.Token);
                if (_stop.IsCancellationRequested || DateTimeOffset.UtcNow >= ExpiresAt) return;
                result = await SendAsync("auth/device/status", new { deviceCode = code });
                if (result.Status == 429) { Fail("rate_limited"); return; }
                if (result.Status == 200 && (result.Json.TryGetProperty("user", out _) || Text(result.Json, "id")?.StartsWith("usr_", StringComparison.Ordinal) == true)) { await ValidateAsync(); return; }
                var error = result.Json.TryGetProperty("error", out value) ? Text(value, "message") : null;
                if (error == "slow_down") { interval = Math.Min(interval + 5, 60); continue; }
                if (result.Status == 200 || error == "authorization_pending") continue;
                Fail(error == "expired_token" ? "expired" : "website_required");
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or InvalidDataException or ObjectDisposedException)
        { Fail("network_error"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_status is "waiting" or "validating" or "ready") _status = "cancelled";
            _stop.Cancel();
            _client.Dispose();
        }
    }
}
