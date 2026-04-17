using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CortexPlexus.Memory;

/// <summary>
/// DI registration for CortexPlexus.Memory services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent memory store, options, and the background reaper.
    /// Requires <see cref="Npgsql.NpgsqlDataSource"/> to be registered already
    /// (done by <c>AddCortexPlexusGraph</c> — we share the same DataSource).
    /// Options default to disabled; callers override via <paramref name="configure"/>.
    /// </summary>
    public static IServiceCollection AddCortexPlexusMemory(
        this IServiceCollection services,
        Action<MemoryOptions>? configure = null)
    {
        services.Configure<MemoryOptions>(opts =>
        {
            configure?.Invoke(opts);
            if (opts.ReapIntervalHours < 1) opts.ReapIntervalHours = 1;
            if (opts.MaxMemoriesPerScope < 1) opts.MaxMemoriesPerScope = 10_000;
            if (opts.DefaultImportance is < 0.0 or > 1.0) opts.DefaultImportance = 0.5;
        });

        services.AddScoped<IAgentMemoryStore, AgentMemoryStore>();

        // The reaper is registered whether or not the feature is enabled — it
        // short-circuits on startup when disabled, so no background work happens.
        // TryAddEnumerable keeps this registration safe against double-calls.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, MemoryReaper>());

        return services;
    }
}
