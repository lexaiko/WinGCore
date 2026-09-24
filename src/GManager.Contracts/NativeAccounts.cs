namespace GManager.Contracts;

public enum NativeSessionStatus { Active, ActionNeeded }
public enum NativeService { Messaging, Identity, Gmail, Drive }
public sealed record NativeSessionSummary(Guid Id, Guid DeviceId, string Email, string AccountId,
    string DisplayName, NativeSessionStatus Status, string? LastError);
public sealed record NativeLoginTicket(string Id, Guid DeviceId, string Url, DateTimeOffset ExpiresAt);
public sealed class NativeGrant
{
    public required string AccessToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public override string ToString() => "NativeGrant [redacted]";
}
