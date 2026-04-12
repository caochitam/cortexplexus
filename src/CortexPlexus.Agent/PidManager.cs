using System.Diagnostics;
using System.Text.Json;

namespace CortexPlexus.Agent;

/// <summary>
/// Manages PID files for running agent instances.
/// Stored in ~/.cortexplexus/agent/pids/
/// </summary>
public static class PidManager
{
    private static readonly string PidDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cortexplexus", "agent", "pids");

    public static void WritePidFile(string name, string path, string server)
    {
        Directory.CreateDirectory(PidDir);
        var pidFile = Path.Combine(PidDir, $"{SanitizeName(name)}.json");
        var info = new PidInfo
        {
            Name = name,
            Pid = Environment.ProcessId,
            Path = path,
            Server = server,
            StartedAt = DateTimeOffset.UtcNow
        };
        File.WriteAllText(pidFile, JsonSerializer.Serialize(info));
    }

    public static void RemovePidFile(string name)
    {
        var pidFile = Path.Combine(PidDir, $"{SanitizeName(name)}.json");
        if (File.Exists(pidFile))
            File.Delete(pidFile);
    }

    public static IReadOnlyList<PidInfo> ListRunningAgents()
    {
        if (!Directory.Exists(PidDir))
            return [];

        var agents = new List<PidInfo>();

        foreach (var file in Directory.GetFiles(PidDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var info = JsonSerializer.Deserialize<PidInfo>(json);
                if (info is null) continue;

                // Check if process is still alive
                try
                {
                    Process.GetProcessById(info.Pid);
                    agents.Add(info);
                }
                catch
                {
                    // Process dead — clean up stale PID file
                    File.Delete(file);
                }
            }
            catch
            {
                // Ignore corrupt PID files
            }
        }

        return agents;
    }

    public static int StopAgents(string? name)
    {
        if (!Directory.Exists(PidDir))
            return 0;

        var stopped = 0;

        foreach (var file in Directory.GetFiles(PidDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var info = JsonSerializer.Deserialize<PidInfo>(json);
                if (info is null) continue;

                if (name is not null && !info.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var process = Process.GetProcessById(info.Pid);
                    process.Kill(entireProcessTree: true);
                    stopped++;
                }
                catch
                {
                    // Already dead
                }

                File.Delete(file);
            }
            catch
            {
                // Ignore
            }
        }

        return stopped;
    }

    private static string SanitizeName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}

public sealed class PidInfo
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public string Path { get; set; } = "";
    public string Server { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
}
