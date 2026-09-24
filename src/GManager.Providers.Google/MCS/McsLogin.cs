namespace GManager.Providers.Google.Mcs;

public sealed class McsLoginException : Exception
{
    public int ErrorCode { get; }
    public McsLoginException(int code, string message) : base($"MCS login rejected by Google: code {code} ({message})")
    {
        ErrorCode = code;
    }
}

public static class McsLogin
{
    public static LoginRequest BuildRequest(
        long androidId,
        long securityToken,
        string? accountId = null,
        string? ac2dmToken = null,
        int sdkVersion = 33)
    {
        var request = new LoginRequest
        {
            Id = $"android-{sdkVersion}",
            Domain = "mcs.android.com",
            User = androidId.ToString(),
            Resource = androidId.ToString(),
            DeviceId = $"android-{androidId:x}",
            AuthToken = securityToken.ToString(),
            AuthService = LoginRequest.Types.AuthService.AndroidId,
            UseRmq2 = true,
            AdaptiveHeartbeat = false,
            NetworkType = 1
        };

        request.Setting.Add(new Setting { Name = "new_vc", Value = "1" });

        if (!string.IsNullOrWhiteSpace(accountId) && long.TryParse(accountId, out var accIdNum))
        {
            request.AccountId = accIdNum;
        }

        return request;
    }

    public static async Task<LoginResponse> PerformLoginAsync(
        IMcsConnection connection,
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(McsConstants.HandshakeTimeout);

        // Send LoginRequest (Tag 2)
        await connection.SendAsync(McsConstants.LoginRequestTag, request, linkedCts.Token);

        // Await LoginResponse (Tag 3)
        var msg = await connection.ReceiveAsync(linkedCts.Token);
        if (msg == null) throw new EndOfStreamException("Connection closed by Google while awaiting LoginResponse.");

        if (msg.Tag != McsConstants.LoginResponseTag || msg.AsLoginResponse is not { } response)
        {
            throw new InvalidDataException($"Expected LoginResponse (tag 3), but received tag {msg.Tag}.");
        }

        if (response.Error != null)
        {
            throw new McsLoginException(response.Error.Code, response.Error.Message ?? "Unknown login error");
        }

        return response;
    }
}
