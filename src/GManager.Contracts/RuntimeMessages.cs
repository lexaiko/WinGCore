using System.Text.Json;
using System.Text.Json.Serialization;

namespace GManager.Contracts;

public enum CheckinOutcome { NeverAttempted, Accepted, Rejected, TransientFailure, InvalidResponse }

public sealed record DeviceSummary(Guid Id, DeviceProfile Profile, DateTimeOffset CreatedAt,
    bool Registered, string? GoogleAndroidId, DateTimeOffset? LastAcceptedAt,
    CheckinOutcome LastOutcome, string? LastError);

public sealed record RuntimeRequest(int Version, string Operation, DeviceProfile? Profile = null, Guid? DeviceId = null,
    string? LoginTicket = null, string? LoginToken = null, Guid? SessionId = null,
    NativeService Service = NativeService.Identity, bool ForceRefresh = false)
{
    public override string ToString() => $"RuntimeRequest {{ Version = {Version}, Operation = {Operation} }}";
}
public sealed record RuntimeResponse(int Version, bool Success, string Code, string Message,
    DeviceSummary[]? Devices = null, NativeSessionSummary[]? Sessions = null,
    NativeLoginTicket? Login = null, NativeGrant? Grant = null)
{
    public override string ToString() => $"RuntimeResponse {{ Version = {Version}, Success = {Success}, Code = {Code} }}";
}

public static class RuntimeProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
