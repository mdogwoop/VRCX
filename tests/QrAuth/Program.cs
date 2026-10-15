using System.Net;
using System.Text;
using System.Text.Json;
using VRCX;

var tests = new (string Name, Func<Task> Run)[] {
    ("Mismatch", Mismatch), ("CookieValidation", CookieValidation), ("Cancel", Cancel),
    ("Expire", Expire), ("Device", Device), ("DeviceRateLimit", DeviceRateLimit),
    ("SlowDown", SlowDown), ("RejectVerificationUrl", RejectVerificationUrl),
    ("RejectUnverifiedAccount", RejectUnverifiedAccount), ("CancelInFlight", CancelInFlight),
    ("ReadyExpiry", ReadyExpiry), ("StopPollingAtExpiry", StopPollingAtExpiry)
};
foreach (var test in tests) { await test.Run(); Console.WriteLine("PASS " + test.Name); }

static HttpResponseMessage Json(int status, string text) => new((HttpStatusCode)status)
{ Content = new StringContent(text, Encoding.UTF8, "application/json") };
static string State(QrLoginOperation operation) => JsonDocument.Parse(operation.Snapshot()).RootElement.GetProperty("status").GetString();
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Until(Func<bool> condition)
{
    for (var i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(10); }
    throw new Exception("Timed out waiting for test state.");
}
static async Task Mismatch()
{
    using var operation = new QrLoginOperation(null, "VRCX-Test", new Transport(_ => Task.FromResult(Json(200, "{\"id\":\"usr_other\"}"))));
    await operation.RestoreAsync("[{\"name\":\"auth\",\"value\":\"test-session\"}]", "usr_expected");
    Check(State(operation) == "failed" && operation.Snapshot().Contains("account_mismatch"), "Mismatched account accepted.");
}
static Task CookieValidation()
{
    var jar = new CookieContainer();
    var expires = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
    using var good = JsonDocument.Parse($"[{{\"name\":\"auth\",\"value\":\"test-token\",\"expires\":{expires}}}]");
    QrLoginOperation.ImportCookies(jar, good.RootElement);
    Check(new DateTimeOffset(jar.GetCookies(QrLoginOperation.Api)["auth"].Expires).ToUnixTimeSeconds() == expires, "Expiry changed.");
    foreach (var json in new[] {
        "[{\"name\":\"auth\",\"value\":\"replacement\"},{\"name\":\"unrelated\",\"value\":\"bad\"}]",
        "[{\"name\":\"auth\",\"value\":\"bad;inject\"}]",
        "[{\"name\":\"auth\",\"value\":\"expired\",\"expires\":1}]"
    })
    {
        using var document = JsonDocument.Parse(json);
        var rejected = false;
        try { QrLoginOperation.ImportCookies(jar, document.RootElement); } catch { rejected = true; }
        Check(rejected, "Invalid cookie accepted.");
        Check(jar.GetCookies(QrLoginOperation.Api)["auth"].Value == "test-token", "Partial cookie import changed the jar.");
    }
    return Task.CompletedTask;
}
static async Task Cancel()
{
    using var operation = new QrLoginOperation(null, "VRCX-Test");
    operation.Dispose();
    var rejected = false;
    try { operation.TakeSession(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected && State(operation) == "cancelled", "Cancelled operation committed.");
}
static async Task Expire()
{
    using var operation = new QrLoginOperation(null, "VRCX-Test");
    operation.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
    Check(State(operation) == "expired", "Expiration not enforced.");
    var rejected = false;
    try { operation.TakeSession(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Expired operation committed.");
}
static async Task Device()
{
    var polls = 0;
    var delays = new List<TimeSpan>();
    using var operation = new QrLoginOperation(null, "VRCX-Test", new Transport(request =>
    {
        var route = request.RequestUri.AbsolutePath;
        if (route.EndsWith("/code")) return Task.FromResult(Json(200, "{\"deviceCode\":\"private-device-code\",\"verificationUrlComplete\":\"https://vrchat.com/home/device?code=TEST\",\"interval\":1,\"expiresIn\":300}"));
        if (route.EndsWith("/status")) { polls++; return Task.FromResult(polls == 1 ? Json(400, "{\"error\":{\"message\":\"authorization_pending\"}}") : Json(200, "{\"user\":{\"id\":\"usr_test\"}}")); }
        return Task.FromResult(Json(200, "{\"id\":\"usr_test\"}"));
    }), (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
    operation.Start(); await Until(() => State(operation) == "ready");
    Check(polls == 2 && delays.All(d => d.TotalSeconds >= 5), "Device polling ignored minimum interval.");
    Check(!operation.Snapshot().Contains("private-device-code"), "Device secret escaped backend.");
}
static async Task DeviceRateLimit()
{
    var requests = 0;
    using var operation = new QrLoginOperation(null, "VRCX-Test", new Transport(_ => { requests++; return Task.FromResult(Json(429, "{}")); }));
    operation.Start(); await Until(() => State(operation) == "failed");
    Check(requests == 1 && operation.Snapshot().Contains("rate_limited"), "Rate-limited code was repeatedly generated.");
}

static async Task SlowDown()
{
    var delays = new List<TimeSpan>();
    var polls = 0;
    using var operation = new QrLoginOperation(null, "VRCX-Test", new Transport(request => Task.FromResult(
        request.RequestUri.AbsolutePath.EndsWith("/code") ? DeviceCode() :
        request.RequestUri.AbsolutePath.EndsWith("/status") ? ++polls == 1
            ? Json(400, "{\"error\":{\"message\":\"slow_down\"}}")
            : Json(200, "{\"user\":{\"id\":\"usr_test\"}}")
        : Json(200, "{\"id\":\"usr_test\"}"))),
        (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
    operation.Start();
    await Until(() => State(operation) == "ready");
    Check(delays.Select(d => d.TotalSeconds).SequenceEqual(new[] { 5d, 10d }), "slow_down did not reduce polling frequency.");
    operation.TakeSession();
    var rejected = false;
    try { operation.TakeSession(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "A session was activated more than once.");
}

static HttpResponseMessage DeviceCode() => Json(200,
    "{\"deviceCode\":\"private-device-code\",\"verificationUrlComplete\":\"https://vrchat.com/home/device?code=TEST\",\"interval\":5,\"expiresIn\":300}");

static async Task RejectVerificationUrl()
{
    foreach (var url in new[] { "http://vrchat.com/home/device", "https://vrchat.com.example.org/home/device", "https://vrchat.com/home/other" })
    {
        var requests = 0;
        using var operation = new QrLoginOperation(null, "VRCX-Test", new Transport(_ =>
        {
            requests++;
            return Task.FromResult(Json(200, JsonSerializer.Serialize(new { deviceCode = "private-device-code", verificationUrlComplete = url })));
        }));
        operation.Start();
        await Until(() => State(operation) == "failed");
        Check(requests == 1 && !operation.Snapshot().Contains("private-device-code"), "Invalid pairing URL was accepted or exposed a secret.");
    }
}

static async Task RejectUnverifiedAccount()
{
    using var operation = new QrLoginOperation(null, "VRCX-Test", new Transport(request => Task.FromResult(
        request.RequestUri.AbsolutePath.EndsWith("/code") ? DeviceCode() :
        request.RequestUri.AbsolutePath.EndsWith("/status") ? Json(200, "{\"user\":{\"id\":\"usr_test\"}}") :
        Json(401, "{\"requiresTwoFactorAuth\":[\"totp\"]}"))), (_, _) => Task.CompletedTask);
    operation.Start();
    await Until(() => State(operation) == "failed");
    var rejected = false;
    try { operation.TakeSession(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "An account without auth/user verification was activated.");
}

static async Task CancelInFlight()
{
    var response = new TaskCompletionSource<HttpResponseMessage>();
    using var operation = new QrLoginOperation(null, "VRCX-Test", new Transport(_ => response.Task));
    operation.Start();
    operation.Dispose();
    response.SetResult(DeviceCode());
    await Task.Delay(20);
    Check(State(operation) == "cancelled", "A late response resurrected a cancelled login.");
}

static async Task ReadyExpiry()
{
    using var operation = new QrLoginOperation(null, "VRCX-Test", new Transport(_ => Task.FromResult(Json(200, "{\"id\":\"usr_test\"}"))));
    await operation.RestoreAsync("[{\"name\":\"auth\",\"value\":\"test-session\"}]", "usr_test");
    Check(State(operation) == "ready", "Restoration failed.");
    operation.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
    var rejected = false;
    try { operation.TakeSession(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Expired ready session was activated.");
}

static async Task StopPollingAtExpiry()
{
    var delay = new TaskCompletionSource();
    var requests = 0;
    using var operation = new QrLoginOperation(null, "VRCX-Test", new Transport(_ =>
    {
        requests++; return Task.FromResult(DeviceCode());
    }), (_, _) => delay.Task);
    operation.Start();
    operation.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
    delay.SetResult();
    await Task.Delay(20);
    Check(requests == 1 && State(operation) == "expired", "Polling continued after expiry.");
}

sealed class Transport(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
}
