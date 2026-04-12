namespace CortexPlexus.Core.Models;

/// <summary>
/// Base record for all code symbols extracted from source code.
/// </summary>
public abstract record CodeSymbol
{
    public required string Fqn { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? FilePath { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public Guid? RepoId { get; init; }

    /// <summary>Developer-written documentation (XML doc, JSDoc, docstring, etc.).</summary>
    public string? Documentation { get; init; }

    /// <summary>AI-generated 1-2 sentence summary of what this symbol does.</summary>
    public string? AiSummary { get; init; }
}

public sealed record ClassInfo : CodeSymbol
{
    public string? Accessibility { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsStatic { get; init; }
    public bool IsSealed { get; init; }
    public bool IsPartial { get; init; }
    public string? BaseTypeFqn { get; init; }
    public IReadOnlyList<string> InterfaceFqns { get; init; } = [];
}

public sealed record MethodInfo : CodeSymbol
{
    public required string Signature { get; init; }
    public string? ReturnType { get; init; }
    public string? Accessibility { get; init; }
    public bool IsAsync { get; init; }
    public bool IsStatic { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public bool IsTestMethod { get; init; }
    public string? ContainingTypeFqn { get; init; }
    public IReadOnlyList<ParameterInfo> Parameters { get; init; } = [];

    // P2c: Code Metrics
    public int? CyclomaticComplexity { get; init; }
    public int? MaxNestingDepth { get; init; }
    public int? LineCount { get; init; }
}

public sealed record InterfaceInfo : CodeSymbol
{
    public string? Accessibility { get; init; }
    public IReadOnlyList<string> MemberFqns { get; init; } = [];
}

public sealed record PropertyInfo : CodeSymbol
{
    public required string Type { get; init; }
    public bool HasGetter { get; init; }
    public bool HasSetter { get; init; }
    public string? ContainingTypeFqn { get; init; }
}

public sealed record ConstructorInfo : CodeSymbol
{
    public required string Signature { get; init; }
    public string? Accessibility { get; init; }
    public string? ContainingTypeFqn { get; init; }
    public IReadOnlyList<ParameterInfo> Parameters { get; init; } = [];
}

public sealed record FieldInfo : CodeSymbol
{
    public required string Type { get; init; }
    public string? Accessibility { get; init; }
    public bool IsStatic { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsConst { get; init; }
    public string? ConstantValue { get; init; }
    public string? ContainingTypeFqn { get; init; }
}

public sealed record EventInfo : CodeSymbol
{
    public required string Type { get; init; }
    public string? Accessibility { get; init; }
    public bool IsStatic { get; init; }
    public string? ContainingTypeFqn { get; init; }
}

public sealed record NamespaceInfo : CodeSymbol;

public sealed record ParameterInfo(string Name, string Type, int Position);
