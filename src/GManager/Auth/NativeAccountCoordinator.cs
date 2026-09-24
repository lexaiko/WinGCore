using GManager.Contracts;
using GManager.Core;
using GManager.Models;

namespace GManager.Auth;

public sealed class NativeAccountCoordinator(NativeRuntimeClient client, Database database)
{
    public static string LocalId(Guid sessionId) => "native:" + sessionId.ToString("N");
    public static bool TryGetSession(string accountId, out Guid id)
    {
        id = default;
        return accountId.StartsWith("native:", StringComparison.Ordinal) && Guid.TryParse(accountId[7..], out id);
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        var sessions = await client.SessionsAsync(token);
        foreach (var session in sessions)
        {
            var id = LocalId(session.Id);
            var account = await database.GetAccountByIdAsync(id) ?? new GoogleAccount { Id = id, Email = session.Email };
            account.DisplayName = session.DisplayName;
            account.State = session.Status == NativeSessionStatus.Active ? AccountState.Active : AccountState.ActionNeeded;
            await database.UpsertAccountAsync(account);
        }
        var ids = sessions.Select(s => LocalId(s.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var account in await database.GetAllAccountsAsync())
            if (TryGetSession(account.Id, out _) && !ids.Contains(account.Id)) await database.DeleteAccountAsync(account.Id);
    }
}
