using CortexPlexus.Core.Abstractions;
using CortexPlexus.Search.QueryExpansion;
using CortexPlexus.Search.Summary;
using Microsoft.Extensions.DependencyInjection;

namespace CortexPlexus.Search;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCortexPlexusSearch(this IServiceCollection services)
    {
        services.AddScoped<HybridQueryRouter>();
        services.AddSingleton<ContextCompressor>();
        return services;
    }

    public static IServiceCollection AddQueryExpansion(
        this IServiceCollection services,
        Action<QueryExpansionOptions>? configure = null)
    {
        var options = new QueryExpansionOptions();
        configure?.Invoke(options);
        services.Configure<QueryExpansionOptions>(o =>
        {
            o.Enabled = options.Enabled;
            o.Provider = options.Provider;
            o.OllamaBaseUrl = options.OllamaBaseUrl;
            o.OllamaModel = options.OllamaModel;
            o.MultiQueryVariants = options.MultiQueryVariants;
            o.TimeoutSeconds = options.TimeoutSeconds;
        });

        if (options.Enabled && options.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient(nameof(OllamaQueryExpander));
            services.AddSingleton<IQueryExpander, OllamaQueryExpander>();
        }
        else
        {
            services.AddSingleton<IQueryExpander, NoOpQueryExpander>();
        }

        return services;
    }

    public static IServiceCollection AddSummaryGeneration(
        this IServiceCollection services,
        Action<SummaryOptions>? configure = null)
    {
        var options = new SummaryOptions();
        configure?.Invoke(options);
        services.Configure<SummaryOptions>(o =>
        {
            o.Enabled = options.Enabled;
            o.Provider = options.Provider;
            o.OllamaBaseUrl = options.OllamaBaseUrl;
            o.OllamaModel = options.OllamaModel;
            o.ApiKey = options.ApiKey;
            o.ApiBaseUrl = options.ApiBaseUrl;
            o.Model = options.Model;
            o.MaxConcurrency = options.MaxConcurrency;
            o.TimeoutSeconds = options.TimeoutSeconds;
        });

        if (options.Enabled && options.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient(nameof(OllamaSummaryGenerator));
            services.AddSingleton<ISummaryGenerator, OllamaSummaryGenerator>();
        }
        else
        {
            services.AddSingleton<ISummaryGenerator, NoOpSummaryGenerator>();
        }

        return services;
    }
}
