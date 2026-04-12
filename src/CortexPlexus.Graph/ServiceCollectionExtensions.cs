using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CortexPlexus.Graph;

/// <summary>
/// DI registration for CortexPlexus.Graph services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all CortexPlexus.Graph stores and the NpgsqlDataSource (with pgvector enabled)
    /// into the service collection.
    /// </summary>
    public static IServiceCollection AddCortexPlexusGraph(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Register NpgsqlDataSource as singleton with pgvector type mapping enabled
        services.AddSingleton<NpgsqlDataSource>(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseVector();
            return builder.Build();
        });

        // Register store implementations as scoped services
        services.AddScoped<IGraphStore, AgeGraphStore>();
        services.AddScoped<IVectorStore, VectorStore>();
        services.AddScoped<IFullTextStore, FullTextStore>();
        services.AddScoped<IRepositoryStore, RepositoryStore>();

        return services;
    }
}
