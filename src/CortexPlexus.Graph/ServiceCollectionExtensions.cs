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
            // Fresh-DB race: Npgsql probes pg_type on the first DataSource connection
            // and caches the result. If `CREATE EXTENSION vector` has not run yet,
            // the cache records "vector type unknown" and poisons every subsequent
            // read/write through this DataSource (issue #1). Run the extension
            // creation on a raw connection BEFORE building the DataSource so that
            // `vector` is visible the moment Npgsql probes.
            EnsureRequiredExtensions(connectionString);

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

    private static void EnsureRequiredExtensions(string connectionString)
    {
        const int maxAttempts = 30;
        var delay = TimeSpan.FromSeconds(1);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var conn = new NpgsqlConnection(connectionString);
                conn.Open();
                using var cmd = new NpgsqlCommand(
                    "CREATE EXTENSION IF NOT EXISTS vector; CREATE EXTENSION IF NOT EXISTS age;",
                    conn);
                cmd.ExecuteNonQuery();
                return;
            }
            catch (NpgsqlException ex)
            {
                lastError = ex;
                if (attempt < maxAttempts) Thread.Sleep(delay);
            }
        }

        throw new InvalidOperationException(
            $"Failed to ensure required Postgres extensions (vector, age) after {maxAttempts} attempts. " +
            "Verify the CortexPlexus postgres image has pgvector + AGE installed and the user has CREATE privileges.",
            lastError);
    }
}
