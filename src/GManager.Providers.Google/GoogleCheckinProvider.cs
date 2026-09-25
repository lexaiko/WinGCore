// Check-in request construction adapted from microG CheckinClient.java.
// Copyright (C) 2013-2017 microG Project Team. SPDX-License-Identifier: Apache-2.0
// See Protos/NOTICE.md for the pinned revision and local changes.
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using GManager.Contracts;
using GManager.Providers.Google.Protocol;
using GManager.Runtime;

namespace GManager.Providers.Google;

public sealed class GoogleCheckinProvider(HttpClient httpClient) : ICheckinProvider
{
    private const int MaxResponseBytes = 1024 * 1024;
    public static readonly Uri Endpoint = new("https://android.clients.google.com/checkin");

    public static CheckinRequest BuildRequest(DeviceState state, DateTimeOffset now)
    {
        var profile = state.Summary.Profile;
        profile.Validate();
        var previous = state.Registration;
        var build = new CheckinRequest.Types.Checkin.Types.Build
        {
            Fingerprint = profile.Fingerprint, Brand = profile.Brand, Manufacturer = profile.Manufacturer,
            Model = profile.Model, Product = profile.Product, Device = profile.Device, Hardware = profile.Hardware,
            Bootloader = profile.Bootloader, Radio = profile.Radio, SdkVersion = profile.SdkVersion,
            Time = profile.BuildTimeSeconds, ClientId = "android-google", OtaInstalled = false
        };
        var checkin = new CheckinRequest.Types.Checkin
        {
            Build = build, LastCheckinMs = previous?.LastCheckinMs ?? 0, UserNumber = 0, Roaming = "WIFI::"
        };
        var evt = new CheckinRequest.Types.Checkin.Types.Event
        {
            Tag = previous is null ? "event_log_start" : "system_update", TimeMs = now.ToUnixTimeMilliseconds()
        };
        if (previous is not null) evt.Value = "1536,0,-1,NULL";
        checkin.Event.Add(evt);
        var config = new DeviceConfig
        {
            TouchScreen = 3, KeyboardType = 1, Navigation = 1, ScreenLayout = profile.ScreenLayout,
            HasHardKeyboard = false, HasFiveWayNavigation = false,
            WidthPixels = profile.WidthPixels, HeightPixels = profile.HeightPixels,
            DensityDpi = profile.DensityDpi, GlEsVersion = profile.GlEsVersion
        };
        config.NativePlatform.Add(profile.NativePlatforms);
        config.Locale.Add(profile.Locales.Length > 0 ? profile.Locales : [profile.Locale.Replace('_', '-')]);
        config.AvailableFeature.Add(profile.AvailableFeatures);
        config.SharedLibrary.Add(profile.SharedLibraries);
        config.GlExtension.Add(profile.GlExtensions);
        var request = new CheckinRequest
        {
            AndroidId = unchecked((long)(previous?.AndroidId ?? 0)), Digest = previous?.Digest ?? "",
            Checkin = checkin, DeviceConfiguration = config, LoggingId = state.LoggingId,
            Locale = profile.Locale, TimeZone = profile.TimeZone, Version = 3,
            Fragment = previous is null ? 0 : 1
        };
        if (state.Accounts is { Count: > 0 })
        {
            foreach (var account in state.Accounts)
            {
                request.AccountCookie.Add($"[{account.Email}]");
                request.AccountCookie.Add(account.Token);
            }
        }
        else request.AccountCookie.Add("");
        if (profile.OtaCertificates is { Length: > 0 })
            request.OtaCert.Add(profile.OtaCertificates);
        else
            request.OtaCert.Add("71Q6Rn2DDZl1zPDVaaeEHItd");
        if (previous is not null) request.SecurityToken = previous.SecurityToken;
        return request;
    }

    public async Task<CheckinResult> CheckinAsync(DeviceState device, CancellationToken cancellationToken)
    {
        var payload = BuildRequest(device, DateTimeOffset.UtcNow).ToByteArray();
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            await gzip.WriteAsync(payload, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Content = new ByteArrayContent(compressed.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuffer");
        request.Content.Headers.ContentEncoding.Add("gzip");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        var dev = string.IsNullOrWhiteSpace(device.Summary.Profile.Device) ? "komodo" : device.Summary.Profile.Device;
        request.Headers.TryAddWithoutValidation("User-Agent", $"Android-Checkin/2.0 ({dev} {device.Summary.Profile.SdkVersion})");
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = (int)response.StatusCode;
                return new(status is 408 or 429 || status >= 500 ? CheckinOutcome.TransientFailure : CheckinOutcome.Rejected,
                    null, $"Check-in returned HTTP {status}.");
            }
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var encodings = response.Content.Headers.ContentEncoding;
            if (encodings.Any(x => !x.Equals("gzip", StringComparison.OrdinalIgnoreCase)))
                return new(CheckinOutcome.InvalidResponse, null, "Unsupported response encoding.");
            await using var decoded = encodings.Count > 0
                ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) : (Stream)source;
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await decoded.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (body.Length + count > MaxResponseBytes)
                    return new(CheckinOutcome.InvalidResponse, null, "Check-in response exceeds size limit.");
                body.Write(buffer, 0, count);
            }
            var result = CheckinResponse.Parser.ParseFrom(body.ToArray());
            if (!result.HasAndroidId || result.AndroidId == 0 || !result.HasSecurityToken || result.SecurityToken == 0)
                return new(CheckinOutcome.InvalidResponse, null, "Check-in response did not contain registration credentials.");
            if (result.HasStatsOk && !result.StatsOk)
                return new(CheckinOutcome.Rejected, null, "Server did not accept check-in statistics.");
            if (device.Registration is { } old && result.AndroidId != old.AndroidId)
                return new(CheckinOutcome.InvalidResponse, null, "Server returned a different device ID; existing registration preserved.");
            return new(CheckinOutcome.Accepted, new Registration
            {
                AndroidId = result.AndroidId, SecurityToken = result.SecurityToken,
                Digest = result.HasDigest ? result.Digest : device.Registration?.Digest ?? "",
                LastCheckinMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(CheckinOutcome.TransientFailure, null, "Check-in timed out."); }
        catch (HttpRequestException)
        { return new(CheckinOutcome.TransientFailure, null, "Could not reach the check-in service."); }
        catch (InvalidProtocolBufferException)
        { return new(CheckinOutcome.InvalidResponse, null, "Malformed check-in response."); }
        catch (InvalidDataException)
        { return new(CheckinOutcome.InvalidResponse, null, "Malformed compressed response."); }
        catch (IOException)
        { return new(CheckinOutcome.TransientFailure, null, "Check-in response was interrupted."); }
    }
}
