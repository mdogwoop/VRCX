using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VRCX;

public partial class WebApi
{
    private QrLoginOperation _qrLogin;
    private bool _externalSession;
    private string _externalUserId;

    public string BeginQrLogin()
    {
        CancelQrLogin();
        var operation = new QrLoginOperation(ProxySet ? Proxy : null, Program.Version);
        _qrLogin = operation;
        operation.Start();
        return operation.Snapshot();
    }

    public string GetQrLoginState(string operationId) =>
        _qrLogin?.Id == operationId ? _qrLogin.Snapshot() : "{\"status\":\"cancelled\"}";

    public void CancelQrLogin()
    {
        _qrLogin?.Dispose();
        _qrLogin = null;
    }

    public string CommitQrLogin(string operationId)
    {
        if (_qrLogin?.Id != operationId) throw new InvalidOperationException("Login was cancelled.");
        var session = _qrLogin.TakeSession();
        CommitExternalCookieJar(session.Cookies, QrLoginOperation.Text(session.User, "id"));
        var user = session.User.GetRawText();
        CancelQrLogin();
        return user;
    }

    private void CommitExternalCookieJar(CookieContainer cookies, string userId)
    {
        lock (_cookiePersistenceGate)
        {
            // Remove the previous global cookie snapshot before disabling its persistence.
            // The new session must never pass through GetAllCookies(), which rewrites expiry.
            SQLite.Instance.ExecuteNonQuery("DELETE FROM `cookies` WHERE `key` = 'default'");
            _externalSession = true;
            _externalUserId = userId;
            CookieContainer = cookies;
            _cookieDirty = false;
            InitializeHttpClient();
        }
    }

    // Electron calls these methods from its main process, encrypting with safeStorage.
    // The local renderer receives only user metadata from RestoreExternalSession.
#if !LINUX
    [CefSharp.JavascriptIgnore]
#endif
    public string ExportExternalSession(string userId)
    {
        lock (_cookiePersistenceGate)
        {
            if (!_externalSession || userId != _externalUserId) throw new InvalidOperationException("No matching external session.");
            var cookies = CookieContainer.GetCookies(QrLoginOperation.Api).Cast<Cookie>()
                .Where(c => QrLoginOperation.CookieNames.Contains(c.Name) && !c.Expired)
                .Select(c => new
                {
                    name = c.Name,
                    value = c.Value,
                    expires = c.Expires == DateTime.MinValue ? 0 : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds()
                }).ToArray();
            if (!cookies.Any(c => c.name == "auth")) throw new InvalidOperationException("Session expired.");
            return JsonSerializer.Serialize(cookies);
        }
    }

#if !LINUX
    [CefSharp.JavascriptIgnore]
#endif
    public async Task<string> RestoreExternalSession(string json, string userId)
    {
        if (!IsSessionUserId(userId)) return "{\"status\":\"reauthorize\"}";
        using var candidate = new QrLoginOperation(ProxySet ? Proxy : null, Program.Version);
        try
        {
            await candidate.RestoreAsync(json, userId);
            var state = JsonDocument.Parse(candidate.Snapshot());
            using (state)
            {
                if (QrLoginOperation.Text(state.RootElement, "status") != "ready")
                {
                    var error = QrLoginOperation.Text(state.RootElement, "error");
                    return error is "network_error" or "rate_limited"
                        ? "{\"status\":\"retry\",\"error\":\"network_error\"}"
                        : "{\"status\":\"reauthorize\"}";
                }
            }
            var session = candidate.TakeSession();
            CommitExternalCookieJar(session.Cookies, userId);
            return "{\"status\":\"ready\",\"user\":" + session.User.GetRawText() + "}";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { return "{\"status\":\"retry\",\"error\":\"network_error\"}"; }
        catch { return "{\"status\":\"reauthorize\"}"; }
    }

    internal static bool IsSessionUserId(string userId) =>
        userId != null && Regex.IsMatch(userId, @"^usr_[A-Za-z0-9_-]{1,80}$");

#if !LINUX
    private static string SessionPath(string userId)
    {
        if (!IsSessionUserId(userId)) throw new ArgumentException("Invalid user ID.");
        return Path.Combine(Program.AppDataDirectory, "auth-sessions", userId + ".bin");
    }

    public bool SaveExternalSession(string userId)
    {
        var file = SessionPath(userId);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(ExportExternalSession(userId)), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        var temp = file + "." + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return true;
    }

    public async Task<string> LoadExternalSession(string userId)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(SessionPath(userId)), null, DataProtectionScope.CurrentUser);
            return await RestoreExternalSession(Encoding.UTF8.GetString(bytes), userId);
        }
        catch (CryptographicException) { return "{\"status\":\"unavailable\",\"error\":\"storage_unavailable\"}"; }
        catch { return "{\"status\":\"reauthorize\"}"; }
    }

    public void DeleteExternalSession(string userId) => File.Delete(SessionPath(userId));
#endif
}
