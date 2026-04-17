using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CortexPlexus.Memory;

/// <summary>
/// DI registration for CortexPlexus.Memory services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent memory store and options. Requires that
    /// <see cref="Npgsql.NpgsqlDataSource"/> is already registered (done by
    /// <c>AddCortexPlexusGraph</c> — we share the same DataSource). Options
    /// default to disabled; callers override via <paramref name="configure"/>
    /// or environment variables bound in <c>Program.cs</c>.
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
        return services;
    }
}
