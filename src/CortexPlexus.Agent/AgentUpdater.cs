using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CortexPlexus.Agent;

/// <summary>
/// Checks for updates from the CortexPlexus server and self-updates the agent binary.
/// </summary>
public sealed class AgentUpdater
{
    private readonly string _serverUrl;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    public AgentUpdater(string serverUrl, ILogger logger)
        : this(serverUrl, logger, new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
    {
    }

    /// <summary>Test-friendly constructor: cho phép inject HttpClient mock.</summary>
    internal AgentUpdater(string serverUrl, ILogger logger, HttpClient httpClient)
    {
        _serverUrl = serverUrl;
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Parse /api/agent/version response JSON. Returns (latestVersion, expectedHash for current platform).
    /// Pure function — testable without HTTP.
    /// </summary>
    internal static (string? latestVersion, string? expectedHash) ParseVersionResponse(
        string json, string platform)
    {
        using var doc = JsonDocument.Parse(json);
        var latestVersion = doc.RootElement.TryGetProperty("version", out var v)
            ? v.GetString()
            : null;

        string? expectedHash = null;
        if (doc.RootElement.TryGetProperty("sha256", out var sha256Prop)
            && sha256Prop.ValueKind == JsonValueKind.Object
            && sha256Prop.TryGetProperty(platform, out var hashProp))
        {
            expectedHash = hashProp.GetString();
        }

        return (latestVersion, expectedHash);
    }

    /// <summary>
    /// Decides whether an update is needed given current version and server response.
    /// Returns true if server version != current version (not null, not equal).
    /// </summary>
    internal static bool ShouldUpdate(string? serverVersion, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion)) return false;
        return !string.Equals(serverVersion, currentVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies downloaded bytes match expected SHA256 hash (hex). Case-insensitive compare.
    /// </summary>
    internal static bool VerifySha256(byte[] downloadedBytes, string expectedHash)
    {
        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(downloadedBytes));
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detect current platform RID used by agent download endpoint.
    /// </summary>
    internal static string DetectPlatform() => OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsMacOS() ? "osx-x64"
        : "linux-x64";

    public async Task<bool> CheckAndUpdateAsync()
    {
        try
        {
            var url = $"{_serverUrl.TrimEnd('/')}/api/agent/version";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to check for updates: {Status}", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var latestVersion = doc.RootElement.GetProperty("version").GetString();

            if (latestVersion is null || latestVersion == AgentInfo.Version)
            {
                _logger.LogInformation("Agent is up to date (v{Version})", AgentInfo.Version);
                return false;
            }

            _logger.LogInformation("Update available: v{Current} → v{Latest}", AgentInfo.Version, latestVersion);

            // Detect platform
            var platform = OperatingSystem.IsWindows() ? "win-x64"
                : OperatingSystem.IsMacOS() ? "osx-x64"
                : "linux-x64";

            var binaryName = OperatingSystem.IsWindows() ? "cortexplexus-agent.exe" : "cortexplexus-agent";
            var installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cortexplexus", "agent");

            // Get expected hash from version endpoint
            string? expectedHash = null;
            try
            {
                if (doc.RootElement.TryGetProperty("sha256", out var sha256Prop) &&
                    sha256Prop.TryGetProperty(platform, out var hashProp))
                {
                    expectedHash = hashProp.GetString();
                }
            }
            catch { /* hash not available — skip verification */ }

            var downloadUrl = $"{_serverUrl.TrimEnd('/')}/api/agent/download?platform={platform}";
            _logger.LogInformation("Downloading from {Url}...", downloadUrl);

            var binaryResponse = await _httpClient.GetAsync(downloadUrl);
            if (!binaryResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Download failed: {Status}", binaryResponse.StatusCode);
                return false;
            }

            var tempPath = Path.Combine(installDir, $"{binaryName}.update");
            await using (var fs = File.Create(tempPath))
            {
                await binaryResponse.Content.CopyToAsync(fs);
            }

            // Verify SHA256 hash if server provided one
            if (expectedHash is not null)
            {
                var downloadedBytes = File.ReadAllBytes(tempPath);
                var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(downloadedBytes));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Hash mismatch! Expected: {Expected}, Got: {Actual}. Aborting update.", expectedHash, actualHash);
                    File.Delete(tempPath);
                    return false;
                }
                _logger.LogInformation("SHA256 verified: {Hash}", actualHash[..16] + "...");
            }

            // Replace current binary
            var currentPath = Path.Combine(installDir, binaryName);
            var backupPath = Path.Combine(installDir, $"{binaryName}.bak");

            if (File.Exists(backupPath))
                File.Delete(backupPath);
            if (File.Exists(currentPath))
                File.Move(currentPath, backupPath);
            File.Move(tempPath, currentPath);

            // Set executable on Unix
            if (!OperatingSystem.IsWindows())
            {
                Process.Start("chmod", $"+x \"{currentPath}\"")?.WaitForExit(5000);
            }

            // Save version
            File.WriteAllText(Path.Combine(installDir, "version.txt"), latestVersion);

            _logger.LogInformation("Updated to v{Version}. Restart agent to use new version.", latestVersion);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            return false;
        }
    }
}
