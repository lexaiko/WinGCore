# Pinned native protocol research

Reference: microg/GmsCore commit c005d992d594b5008dd0ba08719c852a7ae8a6fb.
Reference files are retained under docs/references, with their original notices.
The upstream Apache-2.0 license is retained in the provider's Protos directory.

## Check-in implemented

Source: play-services-core/src/main/java/org/microg/gms/checkin/CheckinClient.java.
Schemas: play-services-core-proto/src/main/proto/checkin.proto and deviceconfig.proto.

Transport is HTTPS POST to android.clients.google.com/checkin using gzip-compressed
Protobuf. The provider uses no desktop OAuth client or token for device-only check-in.
Build properties and display/ABI configuration come from an immutable local profile.
First check-in has no previous security token. Repeat check-in uses the stored
server credentials and last successful check-in time. Logging ID is generated once
per local instance. Both unsigned server identifiers are preserved without losing
precision; the request's androidId field has signed int64 wire semantics.

The runtime accepts registration only after a successful HTTP response containing
nonzero ID and security token, with no explicit statistics rejection. A changed
ID during renewal is reported and not substituted automatically. Response bodies
and registration secrets are never included in errors or CLI output.

Differences from the reference include an explicit Windows user-agent marker,
no fabricated telephony/MAC identifiers and no automatic
background retries. These are experimental interoperability choices. The live
test demonstrates device-only registration for the supplied synthetic profile.

## Account enrollment: implemented, live verification pending

Source files:

- play-services-core/src/main/java/org/microg/gms/auth/login/LoginActivity.java
- play-services-core/src/main/java/org/microg/gms/auth/AuthManager.java
- play-services-base/core/src/main/java/org/microg/gms/auth/AuthRequest.java
- play-services-base/core/src/main/java/org/microg/gms/auth/AuthResponse.java

The reference login activity uses a Google-hosted interactive setup page in an
Android WebView, an Android JavaScript bridge, and a login result cookie. It then
exchanges the result using the Android auth service and stores account credentials
through Android AccountManager. Service grants and account-associated check-in
are subsequent operations. The presence of a web step does not make this the
desktop OAuth authorization-code flow currently in GManager.

The Windows implementation uses an isolated InPrivate WebView2 profile restricted
to HTTPS accounts.google.com, a minimal MinuteMaid bridge, and a single-use runtime
login ticket. The app exchanges the secure oauth_token login-result cookie through
android.googleapis.com/auth. This cookie is produced by the user's explicit login
in GManager; existing browser profiles are not imported. The runtime stores the
resulting master credential with per-session DPAPI protection. No password is stored.

The broker requests ac2dm, identity, Gmail modify, and Drive metadata-readonly grants
through the Android auth endpoint. Account-associated check-in includes email/ac2dm
token pairs. Supported local flows are tested with fixtures; acceptance by Google
and permission to call each service still need live proof. FIDO, DroidGuard,
enterprise external identity providers, and other Android-only challenges are not
implemented. The browser reports these limitations instead of fabricating results.

The current-user named pipe carries the one-time login result into the runtime and
short-lived service grants out to the desktop. Master credentials stay in the runtime.
This is a same-Windows-user trust boundary, not a sandbox against same-user malware.

PIF's Android process modifications and hardware-backed attestation are not
provided by this check-in implementation. Successful registration does not imply
that arbitrary Google service tokens can be obtained or used.
