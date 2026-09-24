using System.IO;
using System.Text.Json;

namespace GManager.Core;

/// <summary>
/// Runtime configuration and path provider for GManager.
/// Stores application data in %LOCALAPPDATA%\GManager.
/// </summary>
public sealed class AppConfig
{
    public const string AppName = "GManager";
    public const string MutexName = @"Local\GManager_App_Mutex_V3";
    public const string IpcPipeName = "GManager_IpcPipe_V3";
    public const string CredentialResource = "GManager";

    // Google OAuth Endpoints
    public const string OAuthAuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string OAuthTokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string OAuthRevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    // Custom URL scheme for iOS-style OAuth redirect (registered in Windows Registry)
    // GManager registers gmanager:// in HKCU so Windows redirects the OAuth callback to us
    public const string OAuthRedirectScheme = "gmanager";
    public const string OAuthRedirectPath = "/oauth/callback";
    public static string OAuthRedirectUri => $"{OAuthRedirectScheme}:{OAuthRedirectPath}";

    // Google API Scopes — identity only (openid + profile + email)
    // These are safe scopes that Google never blocks for any desktop app.
    // Inbox stats and storage info are fetched later via the signed-in browser session.
    public static readonly string[] RequiredScopes =
    [
        "openid",
        "https://www.googleapis.com/auth/userinfo.profile",
        "https://www.googleapis.com/auth/userinfo.email"
    ];

    public static string ScopeString => string.Join(" ", RequiredScopes);

    // Paths
    public string AppDataDirectory { get; }
    public string DatabasePath { get; }
    public string AvatarsDirectory { get; }
    public string ConfigFilePath { get; }

    // Built-in OAuth Client ID — used for the seamless out-of-the-box Google Sign-In.
    // This is a registered Desktop Application client ID. No client_secret is sent
    // (PKCE replaces the secret, same as iOS public clients do).
    public const string DefaultClientId = "662287800555-pdiq3r3puob8a44locitndbocua7c30f.apps.googleusercontent.com";

    // OAuth Client Credentials
    private string _clientId = string.Empty;
    public string ClientId
    {
        get => string.IsNullOrWhiteSpace(_clientId) ? DefaultClientId : _clientId;
        set => _clientId = value;
    }
    // ClientSecret intentionally empty — PKCE public-client flow, no secret needed
    public string ClientSecret { get; set; } = string.Empty;
    public int SyncIntervalMinutes { get; set; } = 5;
    public bool AutoStartEnabled { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public string ThemeMode { get; set; } = "System"; // System, Light, Dark

    public AppConfig()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AppDataDirectory = Path.Combine(localAppData, AppName);
        DatabasePath = Path.Combine(AppDataDirectory, "gmanager.db");
        AvatarsDirectory = Path.Combine(AppDataDirectory, "avatars");
        ConfigFilePath = Path.Combine(AppDataDirectory, "gmanager.config.json");

        EnsureDirectories();
        LoadConfig();
    }

    public void EnsureDirectories()
    {
        if (!Directory.Exists(AppDataDirectory))
        {
            Directory.CreateDirectory(AppDataDirectory);
        }
        if (!Directory.Exists(AvatarsDirectory))
        {
            Directory.CreateDirectory(AvatarsDirectory);
        }
    }

    public void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var dto = JsonSerializer.Deserialize<AppConfigDto>(json);
                if (dto != null)
                {
                    ClientId = dto.ClientId ?? string.Empty;
                    ClientSecret = dto.ClientSecret ?? string.Empty;
                    SyncIntervalMinutes = dto.SyncIntervalMinutes > 0 ? dto.SyncIntervalMinutes : 5;
                    AutoStartEnabled = dto.AutoStartEnabled;
                    MinimizeToTrayOnClose = dto.MinimizeToTrayOnClose;
                    NotificationsEnabled = dto.NotificationsEnabled;
                    ThemeMode = !string.IsNullOrWhiteSpace(dto.ThemeMode) ? dto.ThemeMode : "System";
                }
            }
            else
            {
                // Fallback check to environment variables
                ClientId = Environment.GetEnvironmentVariable("GMANAGER_CLIENT_ID") ?? string.Empty;
                ClientSecret = Environment.GetEnvironmentVariable("GMANAGER_CLIENT_SECRET") ?? string.Empty;
            }
        }
        catch
        {
            // Fallback to defaults on corrupt config file
        }
    }

    public void SaveConfig()
    {
        try
        {
            EnsureDirectories();
            var dto = new AppConfigDto
            {
                ClientId = ClientId,
                ClientSecret = ClientSecret,
                SyncIntervalMinutes = SyncIntervalMinutes,
                AutoStartEnabled = AutoStartEnabled,
                MinimizeToTrayOnClose = MinimizeToTrayOnClose,
                NotificationsEnabled = NotificationsEnabled,
                ThemeMode = ThemeMode
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json);
        }
        catch
        {
            // Best effort save
        }
    }

    private sealed class AppConfigDto
    {
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public int SyncIntervalMinutes { get; set; }
        public bool AutoStartEnabled { get; set; }
        public bool MinimizeToTrayOnClose { get; set; } = true;
        public bool NotificationsEnabled { get; set; } = true;
        public string? ThemeMode { get; set; }
    }
}
