# GManager

GManager contains a Windows WPF Google account application and an experimental
native Windows runtime inspired by microG's device/service architecture.

The native runtime implements persistent virtual-device profiles, Google device
check-in, interactive Android-style account enrollment, and a per-session service
token broker. The WPF app manages devices and native sessions through a local named
pipe. Device-only check-in has been verified against Google; live account enrollment
and service access still require verification with a test account.

This is an experimental Windows protocol implementation. It does not execute
Android APKs or implement Play Integrity attestation.

## Sign in from the desktop app

Build and open `src/GManager/bin/Debug/net8.0-windows10.0.19041.0/GManager.exe`.
Choose **Devices and accounts**, create a lab device or import a profile, then
**Register / check in** and **Sign in with Google**. Enter credentials and challenges
directly on Google's page. Microsoft Edge WebView2 Runtime is required.

After enrollment, choose **Test service access** to request service grants, then
**Use selected account** to open that account in the main app. Grant issuance and
successful Gmail/Drive API calls are separate checks. Unsupported Google challenges
are reported; successful device registration alone does not establish an account.
Removing a local session deletes its local credential, not the Google account or
server-side authorization. Existing desktop OAuth accounts remain a separate path.

## Build and test

Requires Windows and .NET 8 SDK. Run from the repository root:

```powershell
dotnet build GManager.sln
dotnet test GManager.sln
```

## Run the native runtime

Build first, then start the service without a visible console window:

```powershell
$runtimeExe = (Resolve-Path 'src/GManager.RuntimeHost/bin/Debug/net8.0-windows/GManager.RuntimeHost.exe').Path
Start-Process -FilePath $runtimeExe -ArgumentList 'serve' -WindowStyle Hidden
& $runtimeExe status
& $runtimeExe create profiles/windows-lab.json
& $runtimeExe list
```

Use the local `id` returned by `create`:

```powershell
& $runtimeExe checkin <device-id>
& $runtimeExe stop
```

`checkin` contacts Google. `create`, `list`, and `status` are local operations.
Creating a profile does not register it. The supplied lab profile is synthetic;
its declared Android properties do not represent Windows hardware attestation.

Add `--data-dir <directory>` to every command, including `serve`, to use an isolated
runtime. Default storage is `%LOCALAPPDATA%/GManager/runtime/runtime.db`, separate
from the existing application's database. A file lease prevents two hosts from
opening the same runtime directory. The named pipe is scoped to the Windows user
and data directory and accepts only the current user.

Registration credentials are encrypted using Windows DPAPI CurrentUser and a
device-specific context. Do not expect a copied database to decrypt on a different
Windows account/machine. Credential corruption is an error; it does not silently
generate a replacement identity. Same-user applications are within the trust
boundary; this is not isolation from malware running as that user.

Profiles are immutable snapshots in this first version. `create` makes a new
instance, even for an identical profile. `list` returns at most the first 100
devices. IDs are returned as decimal strings to preserve unsigned 64-bit values
in JSON consumers. `registered` means credentials were accepted and stored in a
previous check-in; inspect `lastOutcome` to see whether the latest attempt worked.
Failures preserve earlier registration credentials for a later explicit retry.

## Verification

```powershell
./scripts/Test-NativeRuntime.ps1
./scripts/Test-NativeRuntime.ps1 -Live
```

The first command tests creation and persistence across actual process restarts.
`-Live` additionally performs a first Google check-in and a repeat after restart,
checking that the Google ID is unchanged. It creates one isolated synthetic device
per run, stops its helper process, and leaves a sanitized report in `Temp/`.
Do not use the live test as a polling loop: each invocation creates a new device.

Verified on 2026-09-24: two live check-ins accepted with the same Google ID across
a process restart. See [verification notes](docs/native-runtime-verification.md).
This does not prove account enrollment or Play Integrity compatibility.

## Architecture and next work

- [Native runtime direction](docs/native-runtime-direction.md)
- [Pinned protocol research](docs/native-protocol-research.md)
- [microG attribution](src/GManager.Providers.Google/Protos/NOTICE.md)

Native master credentials are DPAPI-protected per session and stay in the runtime.
The one-time login result travels from the origin-restricted sign-in window to the
runtime; short-lived grants travel back to the desktop for API calls. Both use the
current-user pipe. Other processes running as that Windows user are inside this
trust boundary. Passwords are not used as stored session credentials.

The broker isolates grants by session and service, refreshes expired grants, and
marks revoked sessions as requiring action. Desktop OAuth tokens are not migrated
into native sessions. Live enrollment and service compatibility remain release
verification work, not capabilities proven by the unit tests.
