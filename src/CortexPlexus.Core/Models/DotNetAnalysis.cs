namespace CortexPlexus.Core.Models;

// --- EF Core ---

public sealed record DbContextInfo : CodeSymbol
{
    public IReadOnlyList<DbSetInfo> DbSets { get; init; } = [];
}

public sealed record DbSetInfo(string EntityTypeFqn, string PropertyName, string? TableName);

public sealed record EntityRelationship(
    string FromEntityFqn,
    string ToEntityFqn,
    string RelationType,      // "HasOne", "HasMany", "ManyToMany"
    string? ForeignKeyProperty
);

// --- DI Container ---

public sealed record DiRegistrationInfo : CodeSymbol
{
    public required string ServiceTypeFqn { get; init; }
    public required string ImplementationTypeFqn { get; init; }
    public required string Lifetime { get; init; }    // "Scoped", "Transient", "Singleton"
    public string? ModuleName { get; init; }
}

// --- ASP.NET Endpoints ---

public sealed record ApiEndpointInfo : CodeSymbol
{
    public required string HttpMethod { get; init; }  // "GET", "POST", "PUT", "DELETE"
    public required string RouteTemplate { get; init; }
    public string? HandlerMethodFqn { get; init; }
    public string? EndpointName { get; init; }
    public string? Summary { get; init; }
    public string? ModuleName { get; init; }
}

// --- Middleware Pipeline ---

public sealed record MiddlewareInfo : CodeSymbol
{
    /// <summary>Order in the pipeline (0-based).</summary>
    public required int Order { get; init; }
}

// --- Configuration ---

public sealed record ConfigKeyInfo : CodeSymbol
{
    /// <summary>Source of this config key: "appsettings", "env", "docker-compose", "code".</summary>
    public required string Provider { get; init; }
}

// --- NuGet ---

public sealed record NuGetPackageInfo(
    string PackageId,
    string Version,
    string? ProjectName,
    bool IsVulnerable = false,
    string? LatestVersion = null,
    string? VulnerabilityDescription = null
);
