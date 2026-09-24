namespace GManager.Models;

/// <summary>
/// Represents the health and lifecycle state of an authenticated Google account.
/// Mirrors the iOS Google Account Manager state machine:
/// - Active: Token is valid and ready for API operations.
/// - ActionNeeded: Refresh failed or consent was revoked (invalid_grant); user re-auth needed.
/// - Removing: Account is pending deletion and cleanup.
/// </summary>
public enum AccountState
{
    Active = 0,
    ActionNeeded = 1,
    Removing = 2
}
