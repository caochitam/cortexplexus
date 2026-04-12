using CortexPlexus.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Roslyn;

internal sealed record ConfigAnalysisResult(
    IReadOnlyList<Relationship> Relationships
);

/// <summary>
/// Detects configuration access patterns in C# code:
/// - IConfiguration["key"], Configuration.GetSection("key"), Configuration.GetValue&lt;T&gt;("key")
/// - IOptions&lt;T&gt; / IOptionsSnapshot&lt;T&gt; / IOptionsMonitor&lt;T&gt; (constructor injection)
/// - services.Configure&lt;T&gt;(configuration.GetSection("key"))
/// - Environment.GetEnvironmentVariable("key")
/// Creates ReadsConfig edges from the containing method/class to the config key.
/// </summary>
internal sealed class ConfigurationAnalyzer : CSharpSyntaxWalker
{
    private static readonly HashSet<string> ConfigurationInterfaces = new(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.Configuration.IConfiguration",
        "Microsoft.Extensions.Configuration.IConfigurationRoot",
        "Microsoft.Extensions.Configuration.IConfigurationSection",
    };

    private static readonly HashSet<string> OptionsInterfaces = new(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.Options.IOptions",
        "Microsoft.Extensions.Options.IOptionsSnapshot",
        "Microsoft.Extensions.Options.IOptionsMonitor",
    };

    private static readonly HashSet<string> ConfigAccessMethods = new(StringComparer.Ordinal)
    {
        "GetSection", "GetValue", "GetRequiredSection",
    };

    // R21 Fix #8: GetConnectionString("X") is sugar for ["ConnectionStrings:X"].
    // Requires special handling because the emitted key includes an implicit prefix.
    private const string GetConnectionStringMethod = "GetConnectionString";

    private readonly SemanticModel _semanticModel;
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _seen = [];

    public ConfigurationAnalyzer(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public ConfigAnalysisResult Analyze(SyntaxTree tree)
    {
        Visit(tree.GetRoot());
        return new ConfigAnalysisResult(_relationships);
    }

    // --- IConfiguration["key"] (indexer access) ---
    public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        if (IsConfigurationExpression(node.Expression) &&
            node.ArgumentList.Arguments.Count == 1 &&
            TryGetStringLiteral(node.ArgumentList.Arguments[0].Expression, out var key))
        {
            AddConfigEdge(node, key, "IConfiguration");
        }

        base.VisitElementAccessExpression(node);
    }

    // --- configuration.GetSection("key"), GetValue<T>("key"), etc. ---
    // --- Environment.GetEnvironmentVariable("key") ---
    // --- services.Configure<T>(config.GetSection("key")) ---
    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;

            // IConfiguration.GetSection / GetValue / GetRequiredSection
            if (ConfigAccessMethods.Contains(methodName) &&
                IsConfigurationExpression(memberAccess.Expression) &&
                node.ArgumentList.Arguments.Count >= 1 &&
                TryGetStringLiteral(node.ArgumentList.Arguments[0].Expression, out var configKey))
            {
                AddConfigEdge(node, configKey, "IConfiguration");
            }

            // R21 Fix #8: Configuration.GetConnectionString("X") maps to config key
            // "ConnectionStrings:X". Extension method on IConfiguration defined in
            // Microsoft.Extensions.Configuration.ConfigurationExtensions.
            if (methodName == GetConnectionStringMethod &&
                IsConfigurationExpression(memberAccess.Expression) &&
                node.ArgumentList.Arguments.Count >= 1 &&
                TryGetStringLiteral(node.ArgumentList.Arguments[0].Expression, out var connName))
            {
                AddConfigEdge(node, $"ConnectionStrings:{connName}", "IConfiguration");
            }

            // Environment.GetEnvironmentVariable("KEY")
            if (methodName == "GetEnvironmentVariable" &&
                node.ArgumentList.Arguments.Count >= 1 &&
                TryGetStringLiteral(node.ArgumentList.Arguments[0].Expression, out var envKey))
            {
                var symbol = _semanticModel.GetSymbolInfo(memberAccess).Symbol;
                if (symbol?.ContainingType?.ToDisplayString() == "System.Environment")
                {
                    AddConfigEdge(node, envKey, "env");
                }
            }

            // services.Configure<T>(config.GetSection("key"))
            if (methodName == "Configure" &&
                memberAccess.Name is GenericNameSyntax genericName &&
                genericName.TypeArgumentList.Arguments.Count == 1)
            {
                var optionsType = _semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type;
                if (optionsType is not null && node.ArgumentList.Arguments.Count >= 1)
                {
                    // Try to extract section name from GetSection argument
                    if (node.ArgumentList.Arguments[0].Expression is InvocationExpressionSyntax getSectionCall &&
                        getSectionCall.Expression is MemberAccessExpressionSyntax getSectionAccess &&
                        getSectionAccess.Name.Identifier.Text == "GetSection" &&
                        getSectionCall.ArgumentList.Arguments.Count >= 1 &&
                        TryGetStringLiteral(getSectionCall.ArgumentList.Arguments[0].Expression, out var sectionKey))
                    {
                        AddConfigEdge(node, sectionKey, "IOptions",
                            new Dictionary<string, string> { ["optionsType"] = optionsType.ToDisplayString() });
                    }
                }
            }
        }

        base.VisitInvocationExpression(node);
    }

    // --- IOptions<T> constructor parameter injection ---
    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        foreach (var parameter in node.ParameterList.Parameters)
        {
            if (parameter.Type is null) continue;
            var typeInfo = _semanticModel.GetTypeInfo(parameter.Type);
            var namedType = typeInfo.Type as INamedTypeSymbol;
            if (namedType is null) continue;

            // Check if it's IOptions<T> / IOptionsSnapshot<T> / IOptionsMonitor<T>
            var originalDef = namedType.OriginalDefinition?.ToDisplayString();
            if (originalDef is not null &&
                OptionsInterfaces.Any(o => originalDef.StartsWith(o, StringComparison.Ordinal)) &&
                namedType.TypeArguments.Length == 1)
            {
                var optionsType = namedType.TypeArguments[0];
                var sectionName = optionsType.Name; // Convention: class name = section name
                var containingType = GetContainingTypeFqn(node);
                if (containingType is null) continue;

                var configFqn = $"config:{sectionName}";
                var edgeKey = $"{containingType}->{configFqn}";
                if (_seen.Add(edgeKey))
                {
                    _relationships.Add(new Relationship(
                        containingType,
                        configFqn,
                        RelationshipType.ReadsConfig,
                        new Dictionary<string, string>
                        {
                            ["provider"] = "IOptions",
                            ["optionsType"] = optionsType.ToDisplayString()
                        }));
                }
            }
        }

        base.VisitConstructorDeclaration(node);
    }

    private bool IsConfigurationExpression(ExpressionSyntax expression)
    {
        var typeInfo = _semanticModel.GetTypeInfo(expression);
        var type = typeInfo.Type ?? typeInfo.ConvertedType;
        if (type is null) return false;

        var displayString = type.ToDisplayString();
        if (ConfigurationInterfaces.Contains(displayString))
            return true;

        // Also check interfaces implemented by the type
        if (type is INamedTypeSymbol namedType)
        {
            foreach (var iface in namedType.AllInterfaces)
            {
                if (ConfigurationInterfaces.Contains(iface.ToDisplayString()))
                    return true;
            }
        }

        return false;
    }

    private void AddConfigEdge(SyntaxNode node, string configKey, string provider,
        Dictionary<string, string>? extraMetadata = null)
    {
        var containingMethod = GetContainingMethodFqn(node);
        var fromFqn = containingMethod ?? GetContainingTypeFqn(node);
        if (fromFqn is null) return;

        var configFqn = provider == "env" ? $"env:{configKey}" : $"config:{configKey}";
        var edgeKey = $"{fromFqn}->{configFqn}";
        if (!_seen.Add(edgeKey)) return;

        var metadata = new Dictionary<string, string> { ["provider"] = provider };
        if (extraMetadata is not null)
        {
            foreach (var kv in extraMetadata)
                metadata[kv.Key] = kv.Value;
        }

        _relationships.Add(new Relationship(fromFqn, configFqn, RelationshipType.ReadsConfig, metadata));
    }

    private string? GetContainingMethodFqn(SyntaxNode node)
    {
        var methodNode = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (methodNode is null) return null;

        var symbol = _semanticModel.GetDeclaredSymbol(methodNode);
        return symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    }

    private string? GetContainingTypeFqn(SyntaxNode node)
    {
        var typeNode = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (typeNode is null) return null;

        var symbol = _semanticModel.GetDeclaredSymbol(typeNode);
        return symbol?.ToDisplayString();
    }

    private static bool TryGetStringLiteral(ExpressionSyntax expression, out string value)
    {
        // Direct string literal: "key"
        if (expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            value = literal.Token.ValueText;
            return true;
        }

        // Interpolated string with no interpolations: $"key"
        if (expression is InterpolatedStringExpressionSyntax interpolated &&
            interpolated.Contents.Count == 1 &&
            interpolated.Contents[0] is InterpolatedStringTextSyntax text)
        {
            value = text.TextToken.ValueText;
            return true;
        }

        value = "";
        return false;
    }
}
