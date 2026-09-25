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
                    return await CheckinAsync(id, cancellationToken);
                case "sessions" when accounts is not null:
                    return new(1, true, "Sessions", "Native account sessions.", Sessions: accounts.List());
                case "begin-login" when accounts is not null && request.DeviceId is { } loginDevice:
                    return new(1, true, "LoginReady", "Sign in on the Google page.", Login: accounts.BeginLogin(loginDevice));
                case "cancel-login" when accounts is not null && request.LoginTicket is { } cancelTicket:
                    accounts.CancelLogin(cancelTicket);
                    return Ok("Cancelled", "Sign-in cancelled.");
                case "complete-login" when accounts is not null && request.LoginTicket is { } ticket && request.LoginToken is { } token:
                    var loginResponse = await accounts.CompleteLoginAsync(ticket, token, cancellationToken);
                    if (!loginResponse.Success || loginResponse.Sessions is not { Length: > 0 } sessions)
                        return loginResponse;
                    var setup = await FinishSetupAsync(sessions[0].Id, cancellationToken);
                    // The credential was saved even when a later step needs retrying.
                    return setup with { Success = true, Code = setup.Success ? "SignedIn" : "AccountSavedSetupPending" };
                case "finish-setup" when accounts is not null && request.SessionId is { } setupId:
                    return await FinishSetupAsync(setupId, cancellationToken);
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

    private async Task<RuntimeResponse> FinishSetupAsync(Guid sessionId, CancellationToken token)
    {
        var session = accounts!.List().FirstOrDefault(x => x.Id == sessionId) ?? throw new KeyNotFoundException();
        var auth = await accounts.SetupAccountAsync(sessionId, token);
        if (!auth.Success || auth.Grant is null)
            return new(1, false, auth.Code, "Account saved. GMS setup: " + auth.Message,
                Sessions: [accounts.List().First(x => x.Id == sessionId)]);
        var checkin = await CheckinAsync(session.DeviceId, token);
        return checkin with
        {
            Message = checkin.Success ? "GMS account setup and account check-in accepted." : "Account saved. Account check-in: " + checkin.Message,
            Sessions = [accounts.List().First(x => x.Id == sessionId)]
        };
    }

    private async Task<RuntimeResponse> CheckinAsync(Guid deviceId, CancellationToken token)
    {
        var state = store.Get(deviceId);
        var sessions = accounts?.List().Where(x => x.DeviceId == deviceId).ToArray() ?? [];
        var cookies = new List<CheckinAccount>();
        foreach (var session in sessions)
        {
            var grant = await accounts!.GetGrantAsync(session.Id, NativeService.Messaging, false, token);
            if (!grant.Success || grant.Grant is null)
            {
                foreach (var pending in sessions.Where(x => x.Status == NativeSessionStatus.AssociationPending))
                    accounts.MarkAssociation(pending.Id, false, "Account check-in blocked: " + grant.Message);
                return Error(grant.Code, grant.Message);
            }
            cookies.Add(new(session.Email, grant.Grant.AccessToken));
        }
        var result = await checkinProvider.CheckinAsync(state with { Accounts = cookies }, token);
        if (result.Outcome == CheckinOutcome.Accepted &&
            (result.Registration is null || result.Registration.AndroidId == 0 || result.Registration.SecurityToken == 0 ||
             state.Registration is { } old && result.Registration.AndroidId != old.AndroidId))
            result = new(CheckinOutcome.InvalidResponse, null, "Missing or inconsistent registration credentials.");
        store.SaveResult(deviceId, result);
        foreach (var session in sessions.Where(x => x.Status == NativeSessionStatus.AssociationPending))
            accounts!.MarkAssociation(session.Id, result.Outcome == CheckinOutcome.Accepted, result.Error);
        return new(1, result.Outcome == CheckinOutcome.Accepted, result.Outcome.ToString(),
            result.Error ?? "Google check-in accepted.", [store.Get(deviceId).Summary]);
    }

    private static RuntimeResponse Ok(string code, string message, DeviceSummary[]? devices = null) =>
        new(RuntimeProtocol.Version, true, code, message, devices);
    private static RuntimeResponse Error(string code, string message) => new(RuntimeProtocol.Version, false, code, message);
}
