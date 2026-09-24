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
    }
}
