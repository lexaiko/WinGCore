using GManager.Contracts;

namespace GManager.Runtime;

public sealed class RuntimeService(IDeviceStore store, ICheckinProvider checkinProvider, NativeAccountBroker? accounts = null)
{
    private readonly SemaphoreSlim _operations = new(1, 1);

    public async Task<RuntimeResponse> HandleAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        if (request.Version != RuntimeProtocol.Version)
            return Error("VersionMismatch", "Unsupported runtime protocol version.");
        await _operations.WaitAsync(cancellationToken);
        try
        {
            switch (request.Operation)
            {
                case "status":
                    return Ok("Ready", accounts is null ? "Native check-in available." : "Native check-in, account enrollment, and service grants available.");
                case "list":
                    return Ok("Devices", "Stored virtual devices.", store.List());
                case "create":
                    if (request.Profile is null) return Error("InvalidRequest", "A profile is required.");
                    request.Profile.Validate();
                    return Ok("Created", "Local device created; no Google registration yet.", [store.Create(request.Profile)]);
                case "checkin":
                    if (request.DeviceId is not { } id || id == Guid.Empty)
                        return Error("InvalidRequest", "A device ID is required.");
                    var state = store.Get(id);
                    if (accounts is not null)
                    {
                        var cookies = new List<CheckinAccount>();
                        foreach (var session in accounts.List().Where(x => x.DeviceId == id))
                        {
                            var grant = await accounts.GetGrantAsync(session.Id, NativeService.Messaging, false, cancellationToken);
                            if (!grant.Success || grant.Grant is null) return Error(grant.Code, grant.Message);
                            cookies.Add(new(session.Email, grant.Grant.AccessToken));
                        }
                        state = state with { Accounts = cookies };
                    }
                    var result = await checkinProvider.CheckinAsync(state, cancellationToken);
                    if (result.Outcome == CheckinOutcome.Accepted &&
                        (result.Registration is null || result.Registration.AndroidId == 0 || result.Registration.SecurityToken == 0))
                        result = new(CheckinOutcome.InvalidResponse, null, "Missing registration credentials.");
                    store.SaveResult(id, result);
                    return new(RuntimeProtocol.Version, result.Outcome == CheckinOutcome.Accepted,
                        result.Outcome.ToString(), result.Error ?? "Google check-in accepted.", [store.Get(id).Summary]);
                case "sessions" when accounts is not null:
                    return new(1, true, "Sessions", "Native account sessions.", Sessions: accounts.List());
                case "begin-login" when accounts is not null && request.DeviceId is { } loginDevice:
                    return new(1, true, "LoginReady", "Sign in on the Google page.", Login: accounts.BeginLogin(loginDevice));
                case "cancel-login" when accounts is not null && request.LoginTicket is { } cancelTicket:
                    accounts.CancelLogin(cancelTicket);
                    return Ok("Cancelled", "Sign-in cancelled.");
                case "complete-login" when accounts is not null && request.LoginTicket is { } ticket && request.LoginToken is { } token:
                    return await accounts.CompleteLoginAsync(ticket, token, cancellationToken);
                case "get-grant" when accounts is not null && request.SessionId is { } sessionId:
                    var auth = await accounts.GetGrantAsync(sessionId, request.Service, request.ForceRefresh, cancellationToken);
                    return new(1, auth.Success, auth.Code, auth.Message, Grant: auth.Grant);
                case "forget-session" when accounts is not null && request.SessionId is { } forgetId:
                    accounts.Forget(forgetId);
                    return Ok("Removed", "Session removed locally. Google-side access is managed in your Google account.");
                default:
                    return Error("Unsupported", "Unknown operation or missing parameters.");
            }
        }
        catch (ArgumentException ex) { return Error("InvalidRequest", ex.Message); }
        catch (KeyNotFoundException) { return Error("NotFound", "Device was not found."); }
        finally { _operations.Release(); }
    }

    private static RuntimeResponse Ok(string code, string message, DeviceSummary[]? devices = null) =>
        new(RuntimeProtocol.Version, true, code, message, devices);
    private static RuntimeResponse Error(string code, string message) => new(RuntimeProtocol.Version, false, code, message);
}
