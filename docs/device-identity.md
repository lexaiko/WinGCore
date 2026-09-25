# Device profile and Google account sessions

GManager is a Windows runtime with a virtual Android protocol identity. A successful
Google check-in confirms that an Android ID was issued for that profile. It does not
certify Windows as a Pixel or promise a particular name in Google Account's device
and session list.

The built-in **New Pixel 9 Pro XL profile** uses model `Pixel 9 Pro XL` and codename
`komodo`. Pixel 9 Pro uses `caiman`. An existing registered device retains the
profile it was created with; changing the preset cannot relabel its existing Google
session. To test a separate Pixel 9 Pro profile, import a complete profile with
matching model, product, device, and fingerprint. GManager rejects mismatched
Pixel 9 Pro / Pixel 9 Pro XL codenames and no longer fills missing PIF fields with
`komodo`. Importing creates a new local device; it does not alter an old one.

The Google Account page can show a *session* created by a browser or app rather than
a physical phone. Its “unknown device” heading and generic “Android device” label
are Google's account presentation, not a reflection of GManager's local profile
label. Google does not publish a contract that maps check-in fields to that label.
The browser-based EmbeddedSetup step and the Android auth exchange are separate
from device check-in. A matching model, fingerprint, user agent, or login display
name is not a guarantee that Google will render “Pixel 9 Pro” or issue an integrity
verdict.

When testing, compare the profile shown in GManager, the returned Android ID, the
account session status, and whether service grants/API calls succeed. Do not use
the displayed account-session name as the sole success criterion. Remove old local
sessions in GManager when no longer needed; manage server-side sign-out in Google
Account separately.

Sources: [Google Account device and session guidance](https://support.google.com/accounts/answer/3067630),
[Android device codenames](https://source.android.com/docs/core/architecture/16kb-page-size/flash-pixel-with-16kb-kernel),
[Android build ID mapping](https://source.android.com/docs/setup/reference/build-numbers).

## Live observation, 2026-09-25

The user reported a new account session still labelled as an unknown device with
the service label Android device. The running desktop/runtime started after the
updated provider binary was built. Sanitized IPC inventory showed a registered
Pixel 9 Pro XL / komodo profile with an active native account session. One explicit
check-in through that runtime, which includes the device's account grants, returned
Accepted. No new device or login was created by this diagnostic.

This rules out an old running provider binary and a missing local account/device
relationship for this attempt. It does not prove that Google's account-session UI
associates that session with the reported model. The requested Pixel display name
remains unverified and unresolved; repeated profile creation is not a demonstrated
fix. No account email, credential, or server-issued identifier is recorded here.
