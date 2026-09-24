using GManager.Contracts;

namespace GManager.Runtime;

// This type stays inside the runtime/provider/storage boundary. IPC uses DeviceSummary.
public sealed class Registration
{
    public ulong AndroidId { get; init; }
    public ulong SecurityToken { get; init; }
    public string Digest { get; init; } = "";
    public long LastCheckinMs { get; init; }
    public override string ToString() => "Registration [credentials redacted]";
}

public sealed class CheckinAccount(string email, string token)
{
    public string Email { get; } = email;
    public string Token { get; } = token;
    public override string ToString() => "CheckinAccount [redacted]";
}
public sealed record DeviceState(DeviceSummary Summary, long LoggingId, Registration? Registration,
    IReadOnlyList<CheckinAccount>? Accounts = null);
public sealed record CheckinResult(CheckinOutcome Outcome, Registration? Registration, string? Error);

public interface ICheckinProvider
{
    Task<CheckinResult> CheckinAsync(DeviceState device, CancellationToken cancellationToken);
}

public interface IDeviceStore
{
    DeviceSummary Create(DeviceProfile profile);
    DeviceSummary[] List();
    DeviceState Get(Guid id);
    void SaveResult(Guid id, CheckinResult result);
}
