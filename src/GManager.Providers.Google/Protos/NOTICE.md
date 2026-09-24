# microG protocol attribution

Upstream: https://github.com/microg/GmsCore
Pinned commit: c005d992d594b5008dd0ba08719c852a7ae8a6fb

checkin.proto and deviceconfig.proto originate from play-services-core-proto/src/main/proto.
License: Apache-2.0; see LICENSE.microG. Original inline copyright notices are retained.
Local changes: explicit proto2 syntax and C# namespace only.

GoogleCheckinProvider.cs is a C# adaptation of the request construction and transport
described in play-services-core/src/main/java/org/microg/gms/checkin/CheckinClient.java
at the same commit (Copyright 2013-2017 microG Project Team, Apache-2.0).
It omits Android APIs, persists the logging ID, accepts a supplied profile, bounds
response size, and redacts errors. Account-associated check-in uses ac2dm grants.
The reference Java source is preserved in docs/references/CheckinClient.java.txt.

GoogleNativeAuthProvider.cs and NativeLoginWindow.xaml.cs adapt the protocol and
login bridge described by AuthRequest.java, AuthResponse.java, AuthManager.java,
and LoginActivity.java at that same commit, under Apache-2.0. Reference sources and
their notices are retained in docs/references. Local adaptations use .NET HTTP,
Windows DPAPI, and WebView2; Android-only challenges are unsupported.

The supplied windows-lab.json profile is synthetic test data, not a certified device
identity or a profile known to pass Google integrity checks.
