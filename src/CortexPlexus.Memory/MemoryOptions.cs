namespace CortexPlexus.Memory;

/// <summary>
/// Configuration for the agent memory system. See docs/MEMORY-SYSTEM.md §Enabling memory
/// and ADR-013 for why this defaults to disabled.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>
    /// Master on/off switch. Default false — opt-in. When disabled the MCP tools return
    /// a clear error; the schema migration still runs but the table stays empty.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Reaper scan interval in hours. Minimum 1. Default 24.</summary>
    public int ReapIntervalHours { get; set; } = 24;

    /// <summary>
    /// Hard ceiling on rows per (scope, scope_id) pair. When exceeded the oldest
    /// memories by score are auto-pruned. Default 10000.
    /// </summary>
    public int MaxMemoriesPerScope { get; set; } = 10_000;

    /// <summary>Importance used when a save call does not provide one. Default 0.5.</summary>
    public double DefaultImportance { get; set; } = 0.5;
}
