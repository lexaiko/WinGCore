// Protocol mapping adapted from microG AuthRequest/AuthResponse and LoginActivity.
// Copyright 2013-2023 microG Project Team. SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Net;
using System.Text;
using GManager.Contracts;
using GManager.Runtime;

namespace GManager.Providers.Google;

public sealed class GoogleNativeAuthProvider(HttpClient http) : INativeAuthProvider
{
    public const string Package = "com.google.android.gms";
    public const string Signature = "38918a453d07199354f8b19af05ec6562ced5788";
    public const int ServicesVersion = 250000000;
    public static readonly Uri Endpoint = new("https://android.googleapis.com/auth");

    public async Task<NativeAuthResult> EnrollAsync(DeviceState device, string loginToken, CancellationToken token)
    {
        var fields = BaseFields(device);
        fields["service"] = "ac2dm";
        fields["Token"] = loginToken;
        fields["ACCESS_TOKEN"] = "1";
        fields["add_account"] = "1";
        fields["get_accountid"] = "1";
        fields["source"] = "android";
        fields["droidguard_results"] = "null";
        var (result, values) = await SendAsync(device, fields, token);
        if (result is not null) return result;
        if (!values.TryGetValue("Token", out var master) || string.IsNullOrWhiteSpace(master) ||
            !values.TryGetValue("Email", out var email) || string.IsNullOrWhiteSpace(email))
            return new("InvalidResponse", "Google did not return a complete account session.");
        var name = string.Join(" ", new[] { values.GetValueOrDefault("firstName"), values.GetValueOrDefault("lastName") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new("Accepted", "Native account session accepted.", new NativeCredential
        {
            Email = email, MasterToken = master, AccountId = values.GetValueOrDefault("accountId") ?? email,
            DisplayName = string.IsNullOrWhiteSpace(name) ? email : name
        });
    }

    public async Task<NativeAuthResult> GrantAsync(DeviceState device, NativeCredential credential, NativeService service, CancellationToken token)
    {
        var fields = BaseFields(device);
        if (service == NativeService.Gmail)
        {
            fields["app"] = "com.google.android.gm";
            fields["callerPkg"] = "com.google.android.gm";
        }
        fields["service"] = service switch
        {
            NativeService.Messaging => "ac2dm",
            NativeService.Identity => "oauth2:https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile",
            NativeService.Gmail => "oauth2:https://www.googleapis.com/auth/gmail.readonly",
            NativeService.Drive => "oauth2:https://www.googleapis.com/auth/drive.metadata.readonly",
            _ => throw new ArgumentException("Unsupported native service.")
        };
        fields["Email"] = credential.Email;
        fields["Token"] = credential.MasterToken;
        fields["source"] = "android";
        fields["has_permission"] = "1";
        var (result, values) = await SendAsync(device, fields, token);
        if (result is not null) return result;
        if (!values.TryGetValue("Auth", out var access) || string.IsNullOrWhiteSpace(access))
            return new("InvalidResponse", "Google did not return a service grant.");
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        if (values.TryGetValue("Expiry", out var expiry) && long.TryParse(expiry, out var seconds) && seconds > 0 && seconds <= 253402300799)
            expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        else if (values.TryGetValue("ExpiresInDurationSec", out var duration) && int.TryParse(duration, out var lifetime) && lifetime > 0)
            expires = DateTimeOffset.UtcNow.AddSeconds(Math.Min(lifetime, 86400));
        if (expires <= DateTimeOffset.UtcNow) return new("InvalidResponse", "Google returned an expired service grant.");
        return new("Accepted", "Service grant received.", Grant: new() { AccessToken = access, ExpiresAt = expires });
    }

    private static Dictionary<string, string> BaseFields(DeviceState device)
    {
        if (device.Registration is null) throw new ArgumentException("Device is not registered.");
        var locale = device.Summary.Profile.Locale.Split('_', '-');
        var country = locale.Length > 1 ? locale[1].ToLowerInvariant() : "us";
        return new()
        {
            ["app"] = Package, ["client_sig"] = Signature, ["callerPkg"] = Package, ["callerSig"] = Signature,
            ["androidId"] = device.Registration.AndroidId.ToString("x", CultureInfo.InvariantCulture),
            ["sdk_version"] = device.Summary.Profile.SdkVersion.ToString(CultureInfo.InvariantCulture),
            ["google_play_services_version"] = ServicesVersion.ToString(CultureInfo.InvariantCulture),
            ["lang"] = device.Summary.Profile.Locale, ["device_country"] = country, ["operatorCountry"] = country,
            ["accountType"] = "HOSTED_OR_GOOGLE"
        };
    }

    private async Task<(NativeAuthResult? Error, Dictionary<string, string> Values)> SendAsync(
        DeviceState device, Dictionary<string, string> fields, CancellationToken token)
    {
        var empty = new Dictionary<string, string>();
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.UserAgent.ParseAdd($"GoogleAuth/1.4 ({device.Summary.Profile.Device} {device.Summary.Profile.SdkVersion})");
        var appPkg = fields.GetValueOrDefault("app", Package);
        request.Headers.Add("app", appPkg);
        request.Headers.Add("device", device.Registration!.AndroidId.ToString("x", CultureInfo.InvariantCulture));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)
                return (new("TransientFailure", "Google authentication is temporarily unavailable."), empty);
            await using var source = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            int length;
            while ((length = await source.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + length > 65536) return (new("InvalidResponse", "Authentication response exceeds size limit."), empty);
                bytes.Write(buffer, 0, length);
            }
            var values = ParseResponse(Encoding.UTF8.GetString(bytes.ToArray()));
            if (values.TryGetValue("Error", out var error))
            {
                return (error switch
                {
                    "BadAuthentication" or "AccountDeleted" or "AccountDisabled" => new("ActionNeeded", "Google requires a new sign-in for this account."),
                    "NeedsBrowser" or "CaptchaRequired" or "DeviceManagementRequired" => new("ChallengeRequired", "Google requires an additional sign-in or device challenge. Sign in again; unsupported device challenges cannot be completed here."),
                    "ServiceDisabled" or "Unauthorized" => new("PermissionRequired", "Google did not grant access to this service."),
                    _ => new("Rejected", $"Google rejected the native authentication request ({error}).")
                }, empty);
            }
            if (!response.IsSuccessStatusCode) return (new("Rejected", $"Google authentication returned HTTP {(int)response.StatusCode}."), empty);
            if (values.GetValueOrDefault("issueAdvice") is "consent" or "remote_consent")
                return (new("PermissionRequired", "Google requires additional consent for this service."), empty);
            return (null, values);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return (new("TransientFailure", "Google authentication timed out."), empty); }
        catch (HttpRequestException) { return (new("TransientFailure", "Cannot reach Google authentication."), empty); }
        catch (IOException) { return (new("TransientFailure", "Google authentication response was interrupted."), empty); }
        catch (FormatException) { return (new("InvalidResponse", "Malformed Google authentication response."), empty); }
    }

    public static Dictionary<string, string> ParseResponse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) throw new FormatException();
            if (!values.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim())) throw new FormatException();
        }
        return values;
    }
}
