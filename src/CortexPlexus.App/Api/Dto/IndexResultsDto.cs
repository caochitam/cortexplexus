namespace CortexPlexus.App.Api.Dto;

/// <summary>
/// Payload sent by CortexPlexus.Agent after local parsing.
/// Contains only metadata — no source code bodies.
/// </summary>
public sealed record IndexResultsRequest
{
    public required string ProjectName { get; init; }
    public required IReadOnlyList<SymbolDto> Symbols { get; init; }
    public required IReadOnlyList<RelationshipDto> Relationships { get; init; }
    public required Dictionary<string, string> FileHashes { get; init; }
}

/// <summary>
/// Flat representation of any CodeSymbol subtype.
/// The 'kind' field determines which optional fields are relevant.
/// </summary>
public sealed record SymbolDto
{
    // --- Common (all symbols) ---
    public required string Fqn { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? FilePath { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }

    // --- ClassInfo ---
    public string? Accessibility { get; init; }
    public bool? IsAbstract { get; init; }
    public bool? IsStatic { get; init; }
    public bool? IsSealed { get; init; }
    public bool? IsPartial { get; init; }
    public string? BaseTypeFqn { get; init; }
    public IReadOnlyList<string>? InterfaceFqns { get; init; }

    // --- MethodInfo / ConstructorInfo ---
    public string? Signature { get; init; }
    public string? ReturnType { get; init; }
    public bool? IsAsync { get; init; }
    public bool? IsVirtual { get; init; }
    public bool? IsOverride { get; init; }
    public bool? IsTestMethod { get; init; }
    public string? ContainingTypeFqn { get; init; }
    public IReadOnlyList<ParameterDto>? Parameters { get; init; }

    // --- InterfaceInfo ---
    public IReadOnlyList<string>? MemberFqns { get; init; }

    // --- PropertyInfo ---
    public string? Type { get; init; }
    public bool? HasGetter { get; init; }
    public bool? HasSetter { get; init; }

    // --- Common optional ---
    public string? Documentation { get; init; }
    public string? AiSummary { get; init; }

    // --- DocumentSection ---
    public int? Level { get; init; }
    public string? Content { get; init; }
    public string? DocumentPath { get; init; }

    // --- DbContextInfo ---
    public IReadOnlyList<DbSetDto>? DbSets { get; init; }

    // --- DiRegistrationInfo ---
    public string? ServiceTypeFqn { get; init; }
    public string? ImplementationTypeFqn { get; init; }
    public string? Lifetime { get; init; }
    public string? ModuleName { get; init; }

    // --- ApiEndpointInfo ---
    public string? HttpMethod { get; init; }
    public string? RouteTemplate { get; init; }
    public string? HandlerMethodFqn { get; init; }
    public string? EndpointName { get; init; }
    public string? Summary { get; init; }
}

public sealed record ParameterDto(string Name, string Type, int Position);

public sealed record DbSetDto(string EntityTypeFqn, string PropertyName, string? TableName);

public sealed record RelationshipDto
{
    public required string FromFqn { get; init; }
    public required string ToFqn { get; init; }
    public required string Type { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed record IndexResultsResponse
{
    public required string Project { get; init; }
    public required int Symbols { get; init; }
    public required int Relationships { get; init; }
    public required int Embeddings { get; init; }
    public required double DurationSeconds { get; init; }
}
