using System.Security.Cryptography;
using GManager.Contracts;

namespace GManager.Runtime;

public sealed class NativeCredential
{
    public required string Email { get; init; }
    public required string AccountId { get; init; }
    public required string DisplayName { get; init; }
    public required string MasterToken { get; init; }
    public override string ToString() => "NativeCredential [redacted]";
}

public sealed record NativeSession(NativeSessionSummary Summary, NativeCredential Credential);
public sealed record NativeAuthResult(string Code, string Message, NativeCredential? Credential = null, NativeGrant? Grant = null)
{
    public bool Success => Code == "Accepted";
}

public interface INativeAuthProvider
{
    Task<NativeAuthResult> EnrollAsync(DeviceState device, string loginToken, CancellationToken token);
    Task<NativeAuthResult> GrantAsync(DeviceState device, NativeCredential credential, NativeService service, CancellationToken token);
}

public interface INativeAccountStore
{
    NativeSessionSummary SaveSession(Guid deviceId, NativeCredential credential);
    NativeSession GetSession(Guid sessionId);
    NativeSessionSummary[] ListSessions();
    void SetSessionStatus(Guid sessionId, NativeSessionStatus status, string? error);
    void DeleteSession(Guid sessionId);
}

// RuntimeService serializes calls; access grants remain in memory only.
public sealed class NativeAccountBroker(IDeviceStore devices, INativeAccountStore accounts, INativeAuthProvider provider)
{
    private readonly Dictionary<string, NativeLoginTicket> _pending = new();
    private readonly Dictionary<(Guid, NativeService), NativeGrant> _grants = new();

    public NativeLoginTicket BeginLogin(Guid deviceId)
    {
        var device = devices.Get(deviceId);
        if (device.Registration is null) throw new ArgumentException("Check in this device before signing in.");
        foreach (var expired in _pending.Where(x => x.Value.ExpiresAt < DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
            _pending.Remove(expired);
        if (_pending.Count >= 8) throw new ArgumentException("Too many pending sign-ins. Close an existing sign-in window.");
        var profile = device.Summary.Profile;
        var locale = profile.Locale.Split('_', '-');
        var query = new Dictionary<string, string>
        {
            ["source"] = "android", ["xoauth_display_name"] = "Android Device",
            ["lang"] = locale[0], ["cc"] = locale.Length > 1 ? locale[1].ToLowerInvariant() : "us",
            ["langCountry"] = profile.Locale.ToLowerInvariant(), ["hl"] = profile.Locale.Replace('_', '-'), ["tmpl"] = "new_account"
        };
        var url = "https://accounts.google.com/EmbeddedSetup?" + string.Join("&", query.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}"));
        var ticket = new NativeLoginTicket(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), deviceId, url, DateTimeOffset.UtcNow.AddMinutes(15));
        _pending[ticket.Id] = ticket;
        return ticket;
    }

    public void CancelLogin(string ticket) => _pending.Remove(ticket);

    public async Task<RuntimeResponse> CompleteLoginAsync(string ticketId, string loginToken, CancellationToken token)
    {
        if (!_pending.Remove(ticketId, out var ticket) || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
            return new(1, false, "LoginExpired", "Sign-in expired or was already used. Start again.");
        if (string.IsNullOrWhiteSpace(loginToken) || loginToken.Length > 16384)
            return new(1, false, "InvalidRequest", "Missing or invalid login result.");
        var result = await provider.EnrollAsync(devices.Get(ticket.DeviceId), loginToken, token);
        if (!result.Success || result.Credential is null) return new(1, false, result.Code, result.Message);
        var session = accounts.SaveSession(ticket.DeviceId, result.Credential);
        ClearGrants(session.Id);
        return new(1, true, "SignedIn", "Native account session stored.", Sessions: [session]);
    }

    public async Task<NativeAuthResult> GetGrantAsync(Guid sessionId, NativeService service, bool force, CancellationToken token)
    {
        if (!Enum.IsDefined(service)) throw new ArgumentException("Unsupported service.");
        var session = accounts.GetSession(sessionId);
        if (session.Summary.Status == NativeSessionStatus.ActionNeeded)
            return new("ActionNeeded", "Sign in again to restore this native session.");
        var key = (sessionId, service);
        if (!force && _grants.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return new("Accepted", "Cached service grant.", Grant: cached);
        _grants.Remove(key);
        var result = await provider.GrantAsync(devices.Get(session.Summary.DeviceId), session.Credential, service, token);
        if (result.Success && result.Grant is { } grant) _grants[key] = grant;
        if (result.Code == "ActionNeeded")
        {
            accounts.SetSessionStatus(sessionId, NativeSessionStatus.ActionNeeded, result.Message);
            ClearGrants(sessionId);
        }
        return result;
    }

    public NativeSessionSummary[] List() => accounts.ListSessions();
    public void Forget(Guid sessionId)
    {
        accounts.DeleteSession(sessionId);
        ClearGrants(sessionId);
    }
    private void ClearGrants(Guid id)
    {
        foreach (var key in _grants.Keys.Where(x => x.Item1 == id).ToArray()) _grants.Remove(key);
    }
}
