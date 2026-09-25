# microG account lifecycle adaptation

Reference: microG GmsCore commit `c005d992d594b5008dd0ba08719c852a7ae8a6fb`.
The pinned Java references in `docs/references` retain their original notices;
the provider distributes the Apache-2.0 license and attribution. This document
describes implementation parity, not a claim of complete microG compatibility.

## Account lifecycle

| Stage | microG reference | GManager behavior |
| --- | --- | --- |
| Device registration | CheckinClient / CheckinManager | Profile-based protobuf check-in, stable server ID, DPAPI registration storage |
| Interactive sign-in | LoginActivity / EmbeddedSetup | Google-hosted page in WebView2, same-device accounts exposed to bridge, current profile Android release and model |
| Login completion | closeView, #close, programmatic_auth, signin/continue | Exact Google HTTPS origin checks, bridge callback and SourceChanged/NavigationCompleted handlers, path-scoped cookie lookup |
| Master credential exchange | retrieveRtToken | ac2dm, ACCESS_TOKEN=1, add_account=1, get_accountid=1, explicit null DroidGuard result |
| Local account save | AccountManager | Per-device/session DPAPI record containing master credential, account ID/name, optional SID/LSID/services; no password |
| GMS account enrollment | retrieveGmsToken | Separate SetupAccountAsync with ac2dm, app/caller GMS, system_partition=1, has_permission=1, add_account=1, get_accountid=1, null DroidGuard result; ACCESS_TOKEN is absent |
| Account check-in | checkin(true) | Uses GSF LSid token (app/caller `com.google.android.gsf`, service `ac2dm`); includes email/LSid pairs in check-in protobuf cookies |
| Google Play Device Sync | vending-app / uploadDeviceConfig | Requests `oauth2:https://www.googleapis.com/auth/googleplay` (app `com.android.vending`), serializes hardware specs via protobuf to `https://play-fe.googleapis.com/fdfe/uploadDeviceConfig` |
| Ordinary service tokens | AuthManager | Separate grant path, cache by session/service, expiry handling, force refresh and revocation state |
| Retry | Account lifecycle recovery | finish-setup and sync-play-device IPC/CLI and UI reuse the saved credential; no new device or browser login required unless revoked |

The `system_partition` field mirrors microG's enrollment request convention; it is
not Windows hardware attestation or evidence that Android system services exist.
The explicit null DroidGuard field carries no attestation result. Challenge failures
remain failures. Gmail/Drive access requires Google to accept the service request.

## State and failure handling

`SetupPending` means the local account credential was saved but the dedicated GMS
enrollment has not succeeded. Ordinary grants are blocked until setup succeeds.
`AssociationPending` means a GMS token was received but account check-in remains
incomplete. `Active` means that setup and account check-in completed for the new
flow. `ActionNeeded` means credentials require user sign-in again.

Existing Active sessions from older builds retain their state for compatibility.
Run finish-setup on those sessions to exercise the new protocol sequence. A saved
credential is not discarded when a later network step fails. Login closes with the
persisted pending state, and the device manager reports the pending stage. The UI's
previous duplicate fire-and-forget check-in and swallowed error were removed.

Account setup happens before account-associated check-in. All device accounts must
have usable grants; a failed account is reported instead of being silently omitted.
Retrying setup clears that session's grant cache. Background MCS token acquisition
in RuntimeHost now passes through RuntimeService serialization so it cannot race
account setup's broker cache mutations.

## Profile details

Build.ID is parsed from the fourth slash component of the Android fingerprint,
not the product codename. Android release used by the browser comes from the same
profile. PIF FIRST_API_LEVEL is not interpreted as the current SDK: the importer
uses an explicit current SDK or the fingerprint release. Pixel 9 Pro (`caiman`)
and Pixel 9 Pro XL (`komodo`) profiles cannot silently mix codenames.

Check-in additionally accepts profile-provided feature names, shared libraries,
GL extensions/version, locales and screen layout, matching the corresponding
DeviceConfig fields. Empty lists remain empty; GManager does not infer Android
capabilities from Windows hardware or invent a Pixel system inventory.

## Deliberate limits and remaining gaps

- Windows is not Android AccountManager, PackageManager, telephony, Binder, or an
  APK runtime. The corresponding local lifecycle is implemented in .NET.
- Android-only DroidGuard/FIDO/AFW challenges are not implemented. Do not interpret
  a null result or a changed user agent as an integrity verdict.
- microG's PeopleManager user-information step is not ported. Enrollment's returned
  account ID is retained; basic identity grants are a separate service operation.
- microG's account-change Android broadcasts and GCM account-group registration
  are not yet ported. Current MCS starts for the first stored session at host
  startup; it is not a complete per-device push/account-group implementation.
- The default Pixel XL profile is a local test preset, not a verified full hardware
  inventory. Import actual test profile data for experiments requiring precision.
- Google Account's rendered device/session name remains a separate external
  observation. Successful requests do not prove the session will be named Pixel.

## Verification

Local tests cover enrollment request fields, separation from ordinary grants,
encrypted metadata persistence, retained pending state, retry after process-state
recreation, all-account check-in, rejection without registration loss, origin and
completion URL checks, profile consistency and current SDK parsing.

On 2026-09-25 the runtime executed live verification for the user's existing Pixel
9 Pro XL testing session:
1. GSF LSid check-in grant (`app: com.google.android.gsf`, `service: ac2dm`): Google accepted and returned check-in token.
2. Account-associated device check-in: Google returned `Accepted`.
3. Google Play auth grant (`app: com.android.vending`, `service: oauth2:https://www.googleapis.com/auth/googleplay`): Google accepted and returned Play Store OAuth2 token.
4. Google Play FDFE hardware sync (`https://play-fe.googleapis.com/fdfe/uploadDeviceConfig`): Google Play FDFE accepted the Pixel 9 Pro XL hardware configuration and returned an updated device configuration token.

Normal test runs skip network/account tests. Set GMANAGER_RUN_LIVE_TESTS=1 explicitly
to enable those tests; they use the current Windows user's existing test session.
No master credential, login cookie, SID/LSID or API response body belongs in reports.

Final local validation: 56 runtime tests and 54 desktop tests passed (110 total passed),
with live network/account tests explicitly skipped during normal CI/local test runs.
