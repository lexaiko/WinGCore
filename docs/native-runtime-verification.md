# Native runtime verification

Live observation: 2026-09-24T05:35:02Z.

Environment: Windows host, .NET SDK 8.0.425, standalone GManager.RuntimeHost.
Profile: repository's synthetic profiles/windows-lab.json.
No Google account, password, OAuth client, or Android device was used.

| Check | Observed result |
| --- | --- |
| First native check-in | Accepted, nonzero server-issued ID and security token |
| Runtime process stopped and restarted | Local device ID and profile persisted |
| Second native check-in | Accepted using persisted registration |
| Google device ID before/after restart | Equal |
| Credentials in CLI/report | Security token and digest excluded |

The initial sandboxed attempt could not reach the service. The successful run
used network access outside the sandbox. The live test script stopped its runtime
process afterward. Registrations were retained only in the isolated test database
under the ignored Temp directory, with DPAPI protection for registration secrets.

Automated coverage includes binary protocol field types, a separately authored
response fixture, gzip transport/size limits, server rejection, malformed responses,
credential corruption, persistence, device isolation, protocol versions and IPC.
Final validation: solution build succeeded with zero warnings and zero errors;
The initial milestone passed 17 runtime tests and 30 WPF tests. Subsequent coverage
adds native session encryption and isolation, login ticket consumption/cancellation,
grant caching and revocation, Android auth encoding and redacted errors, origin
validation, and migration of encrypted account/message caches.

Latest local validation: `dotnet test GManager.sln --artifacts-path
C:/coding/gmanager/Temp/validation-native` passed 31 runtime tests and 40 desktop
tests (71 total). A subsequent solution build passed with zero warnings/errors.
The isolated output keeps the user's active sign-in process running. Runtime
packaging resolves the referenced project's actual target directory instead of
assuming the default bin path.

The WPF device manager was launched and used to create a synthetic lab device.
Google accepted its check-in. The interactive sign-in window was opened for the
user to complete login. Account enrollment, account-associated check-in, service
grants, and actual native-token Gmail/Drive calls still await live verification.
Integrity attestation is not implemented. Acceptance of the synthetic profile is
an observation, not a guarantee of future server behavior.

During interactive testing, the user reported a delay after entering email. A
read-only UI observation showed that Google had advanced to the password page.
The host also reported an Android-specific challenge request. This does not yet
establish whether that challenge will prevent completion. No password or 2FA was
entered by the agent. No login URL, account email, cookie, or token is retained in
this verification report.

## Post-consent completion correction

After the user clicked I agree, the Google page remained on a loading indicator
with the terminal fragment `#close`. The desktop had only checked completion in
NavigationCompleted and the bridge callback. It now also checks SourceChanged,
which covers fragment navigation without a new document load. The completion
predicate retains exact HTTPS Google-origin validation. Runtime timeouts during
completion now produce a visible retry message instead of leaving the previous
progress message indefinitely.

Reference: [Microsoft WebView2 navigation events](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/navigation-events).
This correction is built separately while the old sign-in window remains open;
the user can use its existing Finish sign-in button without discarding the flow.

The manual Finish sign-in attempt returned LoginExpired. That rejection occurs
before the provider exchange. The desktop now obtains one replacement ticket for
the same device and retries the existing browser result once, without navigation.
Other authentication failures do not trigger this retry. The running old app needs
to exit before the corrected build can be used. Latest solution tests pass:
32 runtime tests and 48 desktop tests (80 total). Live enrollment is still pending.

## Live Enrollment & Service Grants Verified

Live observation: 2026-09-24T17:17:40Z.

1. Native Account Enrollment: Completed live with device `Windows Lab` and master session saved with DPAPI encryption in SQLite `native_sessions`.
2. Identity & Drive Quota: Live verified with Google Drive API v3 (`about?fields=storageQuota`). Quota received and displayed in desktop UI.
3. Gmail Native Service Grant & API:
   - Identified that requesting tokens with Play Services package `com.google.android.gms` failed Gmail API calls with `403 PERMISSION_DENIED (SERVICE_DISABLED for project 745476177629)`.
   - Configured official Gmail Android package `com.google.android.gm` (signed with Google's canonical cert `38918a453d07199354f8b19af05ec6562ced5788`) and `gmail.readonly` scope for the Gmail service grant.
   - Live tested `https://gmail.googleapis.com/gmail/v1/users/me/messages?q=in:inbox` with the issued bearer token: returned `200 OK` with 201 messages.
4. Profile Sync: Resolved bug where `FetchProfileAsync` replaced `existing.Id` with `userInfo.Sub`, which was creating phantom duplicate accounts without credentials. Account ID is now properly preserved as `native:<guid>`.
5. Full test suite: 83 tests passing (33 Runtime + 50 Desktop).
