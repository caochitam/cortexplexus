using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Roslyn;

internal sealed record DiAnalysisResult(
    IReadOnlyList<DiRegistrationInfo> Registrations
);

internal sealed class DiContainerAnalyzer : CSharpSyntaxWalker
{
    private static readonly HashSet<string> LifetimeMethods = new(StringComparer.Ordinal)
    {
        "AddScoped",
        "AddTransient",
        "AddSingleton",
    };

    private static readonly HashSet<string> SpecialMethods = new(StringComparer.Ordinal)
    {
        "AddHostedService",
        "AddHttpClient",
    };

    private readonly SemanticModel _semanticModel;
    private readonly List<DiRegistrationInfo> _registrations = [];

    public DiContainerAnalyzer(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public DiAnalysisResult Analyze(SyntaxNode root)
    {
        Visit(root);
        return new DiAnalysisResult(_registrations);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
        {
            TryExtractRegistration(node, methodSymbol);
        }

        base.VisitInvocationExpression(node);
    }

    private void TryExtractRegistration(InvocationExpressionSyntax node, IMethodSymbol methodSymbol)
    {
        var methodName = methodSymbol.Name;

        string? lifetime = null;
        bool isSpecial = false;

        if (LifetimeMethods.Contains(methodName))
        {
            // AddScoped -> "Scoped", AddTransient -> "Transient", etc.
            lifetime = methodName["Add".Length..];
        }
        else if (SpecialMethods.Contains(methodName))
        {
            isSpecial = true;
            lifetime = methodName == "AddHostedService" ? "Singleton" : "Transient";
        }
        else
        {
            return;
        }

        // Verify the method is an extension method on IServiceCollection
        if (!IsServiceCollectionMethod(methodSymbol))
            return;

        var typeArguments = methodSymbol.TypeArguments;
        string serviceTypeFqn;
        string implementationTypeFqn;

        if (isSpecial)
        {
            // AddHostedService<T>() and AddHttpClient<T>() have a single type argument
            if (typeArguments.Length >= 1)
            {
                var typeArg = typeArguments[0];
                serviceTypeFqn = SymbolExtractor.GetFqn(typeArg);
                implementationTypeFqn = serviceTypeFqn;
            }
            else if (methodName == "AddHttpClient" && TryGetNameArgument(node, out var clientName))
            {
                // services.AddHttpClient("name") — named HttpClient, no generic type
                serviceTypeFqn = $"System.Net.Http.HttpClient:{clientName}";
                implementationTypeFqn = "System.Net.Http.HttpClient";
            }
            else
            {
                return;
            }
        }
        else if (typeArguments.Length == 2)
        {
            // AddScoped<TService, TImplementation>()
            serviceTypeFqn = SymbolExtractor.GetFqn(typeArguments[0]);
            implementationTypeFqn = SymbolExtractor.GetFqn(typeArguments[1]);
        }
        else if (typeArguments.Length == 1)
        {
            // AddScoped<TImplementation>() — self-registration
            serviceTypeFqn = SymbolExtractor.GetFqn(typeArguments[0]);
            implementationTypeFqn = serviceTypeFqn;
        }
        else
        {
            return;
        }

        var fqn = $"DI:{serviceTypeFqn}->{implementationTypeFqn}";
        var moduleName = GetEnclosingModuleName(node);
        var filePath = node.SyntaxTree.FilePath;
        var lineSpan = node.GetLocation().GetLineSpan();

        _registrations.Add(new DiRegistrationInfo
        {
            Fqn = fqn,
            Name = $"{methodName}<{implementationTypeFqn}>",
            Kind = "di_registration",
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            ServiceTypeFqn = serviceTypeFqn,
            ImplementationTypeFqn = implementationTypeFqn,
            Lifetime = lifetime,
            ModuleName = moduleName,
        });
    }

    private static bool IsServiceCollectionMethod(IMethodSymbol method)
    {
        // Extension methods: check the first parameter type (the 'this' parameter)
        if (method.IsExtensionMethod && method.ReducedFrom is { } original)
        {
            method = original;
        }

        if (method.IsExtensionMethod && method.Parameters.Length > 0)
        {
            var receiverType = method.Parameters[0].Type;
            return IsOrImplementsIServiceCollection(receiverType);
        }

        // Instance method on IServiceCollection itself
        if (method.ReceiverType is not null)
        {
            return IsOrImplementsIServiceCollection(method.ReceiverType);
        }

        return false;
    }

    private static bool IsOrImplementsIServiceCollection(ITypeSymbol type)
    {
        if (IsIServiceCollection(type))
            return true;

        if (type is INamedTypeSymbol namedType)
        {
            foreach (var iface in namedType.AllInterfaces)
            {
                if (IsIServiceCollection(iface))
                    return true;
            }
        }

        return false;
    }

    private static bool IsIServiceCollection(ITypeSymbol type) =>
        type.Name == "IServiceCollection"
        && type.ContainingNamespace?.ToDisplayString() == "Microsoft.Extensions.DependencyInjection";

    private static bool TryGetNameArgument(InvocationExpressionSyntax node, out string name)
    {
        name = string.Empty;
        var arguments = node.ArgumentList.Arguments;
        if (arguments.Count == 0)
            return false;

        var firstArg = arguments[0].Expression;
        if (firstArg is LiteralExpressionSyntax { Token.Value: string literalValue })
        {
            name = literalValue;
            return true;
        }

        return false;
    }

    private string? GetEnclosingModuleName(SyntaxNode node)
    {
        // Walk up to find the enclosing method, then its enclosing class.
        // Module pattern: a static class with extension methods configuring DI.
        var enclosingMethod = node.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (enclosingMethod is null)
            return null;

        var enclosingClass = enclosingMethod.Ancestors()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault();

        if (enclosingClass is null)
            return null;

        if (_semanticModel.GetDeclaredSymbol(enclosingClass) is INamedTypeSymbol classSymbol)
        {
            return classSymbol.IsStatic
                ? classSymbol.Name
                : $"{classSymbol.Name}.{GetMethodName(enclosingMethod)}";
        }

        return enclosingClass.Identifier.Text;
    }

    private static string GetMethodName(MethodDeclarationSyntax method) =>
        method.Identifier.Text;
}
