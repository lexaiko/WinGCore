using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using GManager.Contracts;
using GManager.Providers.Google.Protocol;
using GManager.Runtime;

namespace GManager.Providers.Google;

public sealed class GooglePlaySyncProvider(HttpClient httpClient) : IDeviceSyncProvider
{
    public static readonly Uri Endpoint = new("https://play-fe.googleapis.com/fdfe/uploadDeviceConfig");

    public static UploadDeviceConfigRequest BuildRequest(DeviceState state)
    {
        var profile = state.Summary.Profile;
        profile.Validate();
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

        return new UploadDeviceConfigRequest
        {
            DeviceConfiguration = config,
            Manufacturer = string.IsNullOrWhiteSpace(profile.Manufacturer) ? "Google" : profile.Manufacturer
        };
    }

    public async Task<DeviceSyncResult> UploadDeviceConfigAsync(DeviceState device, string googlePlayToken, CancellationToken cancellationToken)
    {
        if (device.Registration is null)
            return new(false, "DeviceNotRegistered", "Device is not registered.");

        var payload = BuildRequest(device).ToByteArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var profile = device.Summary.Profile;
        var androidId = device.Registration.AndroidId;
        var androidIdHex = androidId.ToString("x", CultureInfo.InvariantCulture);

        var model = Uri.EscapeDataString(string.IsNullOrWhiteSpace(profile.Model) ? "Pixel 9 Pro XL" : profile.Model);
        var dev = string.IsNullOrWhiteSpace(profile.Device) ? "komodo" : profile.Device;
        var hw = string.IsNullOrWhiteSpace(profile.Hardware) ? "komodo" : profile.Hardware;
        var prod = string.IsNullOrWhiteSpace(profile.Product) ? "komodo" : profile.Product;
        var rel = string.IsNullOrWhiteSpace(profile.AndroidRelease) ? "14" : profile.AndroidRelease;
        var buildId = string.IsNullOrWhiteSpace(profile.BuildId) ? "AD1A.240905.004" : profile.BuildId;
        var sdk = profile.SdkVersion > 0 ? profile.SdkVersion : 34;

        request.Headers.TryAddWithoutValidation("User-Agent",
            $"Android-Finsky/37.5.24-29 (api={sdk},versionCode=83752400,sdk={sdk},device={dev},hardware={hw},product={prod},platformVersionRelease={rel},model={model},buildId={buildId},isLowRam=0)");
        request.Headers.TryAddWithoutValidation("X-DFE-Device-Id", androidIdHex);
        request.Headers.TryAddWithoutValidation("X-DFE-Client-Id", "am-google");
        request.Headers.TryAddWithoutValidation("Accept-Language", profile.Locale.Replace('_', '-'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", googlePlayToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return new(false, "Rejected", $"Google Play uploadDeviceConfig returned HTTP {status}.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return new(true, "Accepted", "Google Play device config uploaded.");

            string? token = null;
            try
            {
                var apiResponse = GooglePlayApiResponse.Parser.ParseFrom(bytes);
                token = apiResponse.Payload?.UploadDeviceConfigResponse?.DeviceConfigToken;
            }
            catch
            {
                try
                {
                    var directResponse = UploadDeviceConfigResponse.Parser.ParseFrom(bytes);
                    token = directResponse.DeviceConfigToken;
                }
                catch { }
            }

            return new(true, "Accepted", "Google Play device config registered.", token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "TransientFailure", "Google Play device config upload timed out.");
        }
        catch (HttpRequestException)
        {
            return new(false, "TransientFailure", "Could not reach Google Play FDFE service.");
        }
        catch (Exception ex)
        {
            return new(false, "InvalidResponse", "Failed to parse Google Play response: " + ex.Message);
        }
    }
}
