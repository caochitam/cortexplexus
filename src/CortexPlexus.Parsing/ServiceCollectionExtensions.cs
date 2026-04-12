using CortexPlexus.Core.Abstractions;
using CortexPlexus.Parsing.Markdown;
using CortexPlexus.Parsing.TreeSitter;
using Microsoft.Extensions.DependencyInjection;

namespace CortexPlexus.Parsing;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCortexPlexusParsing(this IServiceCollection services)
    {
        services.AddSingleton<ICodeParser, RoslynCodeParser>();
        services.AddSingleton<TreeSitterCodeParser>();
        services.AddSingleton<MarkdownParser>();
        return services;
    }
}
