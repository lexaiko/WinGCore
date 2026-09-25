namespace GManager.Contracts;

public sealed record DeviceProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required string Name { get; init; }
    public required string Fingerprint { get; init; }
    public required string Brand { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }
    public required string Product { get; init; }
    public required string Device { get; init; }
    public required string Hardware { get; init; }
    public required int SdkVersion { get; init; }
    public long BuildTimeSeconds { get; init; }
    public string Bootloader { get; init; } = "unknown";
    public string Radio { get; init; } = "unknown";
    public string Locale { get; init; } = "en_US";
    public string TimeZone { get; init; } = "Etc/UTC";
    public string[] NativePlatforms { get; init; } = ["x86_64"];
    public string[] OtaCertificates { get; init; } = [];
    public int WidthPixels { get; init; } = 1080;
    public int HeightPixels { get; init; } = 1920;
    public int DensityDpi { get; init; } = 420;
    public int GlEsVersion { get; init; } = 0x30000;
    public int ScreenLayout { get; init; } = 2;
    public string[] AvailableFeatures { get; init; } = [];
    public string[] SharedLibraries { get; init; } = [];
    public string[] GlExtensions { get; init; } = [];
    public string[] Locales { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string BuildId => Fingerprint.Split('/') is { Length: >= 4 } parts && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : "unknown";

    [System.Text.Json.Serialization.JsonIgnore]
    public string AndroidRelease
    {
        get
        {
            var parts = Fingerprint.Split('/');
            if (parts.Length >= 3 && parts[2].Split(':') is { Length: 2 } deviceRelease &&
                deviceRelease[1].Length > 0 && deviceRelease[1].All(x => char.IsAsciiDigit(x) || x == '.'))
                return deviceRelease[1];
            return SdkVersion switch { 36 => "16", 35 => "15", 34 => "14", 33 => "13", 32 or 31 => "12", 30 => "11", 29 => "10", _ => "unknown" };
        }
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("Unsupported profile schema version.");
        foreach (var value in new[] { Name, Fingerprint, Brand, Manufacturer, Model, Product,
                     Device, Hardware, Bootloader, Radio, Locale, TimeZone })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
                throw new ArgumentException("Profile strings must contain 1–512 printable characters.");
        if (SdkVersion is < 1 or > 100 || BuildTimeSeconds < 0 || WidthPixels is < 1 or > 16384 ||
            HeightPixels is < 1 or > 16384 || DensityDpi is < 1 or > 2000)
            throw new ArgumentException("Invalid SDK, build time, or display dimensions.");
        if (NativePlatforms is null || NativePlatforms.Length is < 1 or > 8 ||
            NativePlatforms.Any(x => x is not ("x86" or "x86_64" or "arm64-v8a" or "armeabi-v7a" or "armeabi")))
            throw new ArgumentException("Invalid native platform list.");
        if (OtaCertificates is null || OtaCertificates.Length > 16 ||
            OtaCertificates.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256 || x.Any(char.IsControl)))
            throw new ArgumentException("Invalid OTA certificate list.");
        foreach (var entries in new[] { AvailableFeatures, SharedLibraries, GlExtensions, Locales })
            if (entries is null || entries.Length > 512 || entries.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256 || x.Any(char.IsControl)))
                throw new ArgumentException("Invalid device capability list.");
        if (GlEsVersion < 0 || ScreenLayout < 0) throw new ArgumentException("Invalid graphics or screen configuration.");

        var expectedDevice = Model switch
        {
            "Pixel 9 Pro" => "caiman",
            "Pixel 9 Pro XL" => "komodo",
            "Pixel 10 Pro" => "blazer",
            "Pixel 10 Pro XL" => "mustang",
            _ => null
        };
        if (expectedDevice is not null &&
            (!Device.Equals(expectedDevice, StringComparison.OrdinalIgnoreCase) ||
             !Product.Equals(expectedDevice, StringComparison.OrdinalIgnoreCase) ||
             !Fingerprint.StartsWith($"google/{expectedDevice}/{expectedDevice}:", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{Model} requires device/product/fingerprint codename '{expectedDevice}'. Check the imported profile.");
    }
}
