using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Roslyn;

internal sealed record EfCoreAnalysisResult(
    IReadOnlyList<DbContextInfo> DbContexts,
    IReadOnlyList<EntityRelationship> EntityRelationships,
    IReadOnlyList<Relationship> Relationships
);

internal sealed class EfCoreAnalyzer : CSharpSyntaxWalker
{
    private const string DbContextFqn = "Microsoft.EntityFrameworkCore.DbContext";
    private const string EntityTypeConfigurationFqn = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration";

    private readonly SemanticModel _semanticModel;
    private readonly Compilation _compilation;

    private readonly List<DbContextInfo> _dbContexts = [];
    private readonly List<EntityRelationship> _entityRelationships = [];
    private readonly List<Relationship> _relationships = [];

    public EfCoreAnalyzer(SemanticModel semanticModel, Compilation compilation)
    {
        _semanticModel = semanticModel;
        _compilation = compilation;
    }

    public EfCoreAnalysisResult Analyze(SyntaxTree tree)
    {
        Visit(tree.GetRoot());

        return new EfCoreAnalysisResult(
            _dbContexts,
            _entityRelationships,
            _relationships
        );
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not INamedTypeSymbol classSymbol)
        {
            base.VisitClassDeclaration(node);
            return;
        }

        if (InheritsFromDbContext(classSymbol))
        {
            ExtractDbContext(classSymbol, node);
        }

        if (ImplementsEntityTypeConfiguration(classSymbol, out var configuredEntityType))
        {
            ExtractEntityConfiguration(classSymbol, configuredEntityType!, node);
        }

        base.VisitClassDeclaration(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        TryExtractFluentRelationship(node);
        base.VisitInvocationExpression(node);
    }

    // ---------------------------------------------------------------
    // DbContext detection
    // ---------------------------------------------------------------

    private bool InheritsFromDbContext(INamedTypeSymbol symbol)
    {
        var current = symbol.BaseType;
        while (current is not null)
        {
            if (GetFqn(current) == DbContextFqn)
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private void ExtractDbContext(INamedTypeSymbol classSymbol, ClassDeclarationSyntax node)
    {
        var classFqn = GetFqn(classSymbol);
        var lineSpan = node.GetLocation().GetLineSpan();

        var dbSets = ExtractDbSetProperties(classSymbol);

        // If no DbSet properties, discover entities via IEntityTypeConfiguration<T>
        // implementations in the compilation. This handles the common pattern of
        // ApplyConfigurationsFromAssembly in OnModelCreating.
        if (dbSets.Count == 0)
        {
            dbSets = DiscoverEntitiesFromConfigurations();
        }

        _dbContexts.Add(new DbContextInfo
        {
            Fqn = classFqn,
            Name = classSymbol.Name,
            Kind = "dbcontext",
            FilePath = node.SyntaxTree.FilePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            DbSets = dbSets
        });

        // Emit MAPS_TO edges from DbContext to each entity type
        foreach (var dbSet in dbSets)
        {
            _relationships.Add(new Relationship(
                classFqn,
                dbSet.EntityTypeFqn,
                RelationshipType.MapsTo,
                new Dictionary<string, string> { ["propertyName"] = dbSet.PropertyName }
            ));
        }
    }

    private bool UsesApplyConfigurationsFromAssembly(ClassDeclarationSyntax node)
    {
        return node.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(inv =>
            {
                var symbol = _semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                return symbol?.Name == "ApplyConfigurationsFromAssembly";
            });
    }

    private List<DbSetInfo> DiscoverEntitiesFromConfigurations()
    {
        var dbSets = new List<DbSetInfo>();
        var seen = new HashSet<string>();

        foreach (var syntaxTree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol)
                    continue;

                if (!ImplementsEntityTypeConfiguration(classSymbol, out var entityType) || entityType is null)
                    continue;

                var entityFqn = GetFqn(entityType);
                if (string.IsNullOrWhiteSpace(entityFqn) || !seen.Add(entityFqn))
                    continue;

                dbSets.Add(new DbSetInfo(entityFqn, entityType.Name, TableName: null));
            }
        }

        return dbSets;
    }

    private List<DbSetInfo> ExtractDbSetProperties(INamedTypeSymbol contextSymbol)
    {
        var dbSets = new List<DbSetInfo>();

        foreach (var member in contextSymbol.GetMembers())
        {
            if (member is not IPropertySymbol property)
                continue;

            if (property.Type is not INamedTypeSymbol propType)
                continue;

            if (!IsDbSetType(propType))
                continue;

            // DbSet<T> — extract T
            if (propType.TypeArguments.Length != 1)
                continue;

            var entityType = propType.TypeArguments[0];
            var entityFqn = GetFqn(entityType);

            dbSets.Add(new DbSetInfo(entityFqn, property.Name, TableName: null));
        }

        return dbSets;
    }

    private static bool IsDbSetType(INamedTypeSymbol typeSymbol)
    {
        // Robust check: match by name + arity + (best-effort) namespace.
        //
        // Trước fix: chỉ check `fqn == "Microsoft.EntityFrameworkCore.DbSet<T>"` —
        // failed when:
        // 1. Generic parameter name khác T (TEntity, TItem, etc.)
        // 2. Compilation has errors (NuGet not restored) → ErrorTypeSymbol → wrong FQN
        // 3. EF Core type variants (DbSet`1 vs DbSet<T>)
        //
        // CortexFlow R12: 30+ DbSet properties indexed by AppDbContext nhưng không có
        // MapsTo edges nào emit. Root cause: this overly-strict check.
        if (typeSymbol.Name != "DbSet") return false;
        if (typeSymbol.Arity != 1) return false;

        // Walk up containing namespace chain to verify Microsoft.EntityFrameworkCore.
        // Best-effort — works for both fully-resolved and error-state compilations.
        var ns = typeSymbol.ContainingNamespace;
        if (ns is null) return true; // Name+arity match enough nếu namespace unknown

        var nsName = ns.ToDisplayString();
        return nsName == "Microsoft.EntityFrameworkCore"
            || nsName.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // IEntityTypeConfiguration<T> detection
    // ---------------------------------------------------------------

    private bool ImplementsEntityTypeConfiguration(
        INamedTypeSymbol classSymbol,
        out ITypeSymbol? configuredEntity)
    {
        configuredEntity = null;

        // Strategy 1: Check resolved interfaces (works when EF Core package is fully resolved)
        foreach (var iface in classSymbol.AllInterfaces)
        {
            if (iface.TypeArguments.Length != 1)
                continue;

            var baseName = iface.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty);

            if (baseName.StartsWith(EntityTypeConfigurationFqn))
            {
                configuredEntity = iface.TypeArguments[0];
                return true;
            }
        }

        // Strategy 2: Check base list syntax (works even when EF Core types aren't resolved)
        // Matches patterns like: class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
        var syntax = classSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as ClassDeclarationSyntax;
        if (syntax?.BaseList is not null)
        {
            foreach (var baseType in syntax.BaseList.Types)
            {
                var typeText = baseType.Type.ToString();
                if (typeText.Contains("IEntityTypeConfiguration"))
                {
                    // Try to extract the generic type argument
                    if (baseType.Type is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Count == 1)
                    {
                        var argSyntax = generic.TypeArgumentList.Arguments[0];
                        var typeInfo = _semanticModel.GetTypeInfo(argSyntax);
                        if (typeInfo.Type is not null)
                        {
                            configuredEntity = typeInfo.Type;
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private void ExtractEntityConfiguration(
        INamedTypeSymbol configClass,
        ITypeSymbol configuredEntity,
        ClassDeclarationSyntax node)
    {
        var configFqn = GetFqn(configClass);
        var entityFqn = GetFqn(configuredEntity);

        // CONFIGURES edge: configuration class → entity
        _relationships.Add(new Relationship(
            configFqn,
            entityFqn,
            RelationshipType.Configures
        ));

        // Walk the Configure method body for fluent API calls
        foreach (var methodNode in node.Members.OfType<MethodDeclarationSyntax>())
        {
            if (methodNode.Identifier.Text != "Configure")
                continue;

            ExtractFluentRelationshipsFromMethod(methodNode, entityFqn);
        }
    }

    // ---------------------------------------------------------------
    // Fluent API relationship extraction (best-effort)
    // ---------------------------------------------------------------

    private void ExtractFluentRelationshipsFromMethod(MethodDeclarationSyntax method, string ownerEntityFqn)
    {
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            TryExtractFluentRelationshipFromChain(invocation, ownerEntityFqn);
        }
    }

    private void TryExtractFluentRelationship(InvocationExpressionSyntax invocation)
    {
        // Check if we're inside OnModelCreating
        var enclosingMethod = invocation.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (enclosingMethod is null || enclosingMethod.Identifier.Text != "OnModelCreating")
            return;

        // Find the enclosing class and check if it's a DbContext
        var enclosingClass = enclosingMethod.Ancestors()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault();

        if (enclosingClass is null)
            return;

        if (_semanticModel.GetDeclaredSymbol(enclosingClass) is not INamedTypeSymbol classSymbol)
            return;

        if (!InheritsFromDbContext(classSymbol))
            return;

        // Try to resolve the entity type from the invocation chain.
        // Patterns like: builder.Entity<Order>().HasMany<OrderItem>()...
        TryExtractFluentRelationshipFromChain(invocation, ownerEntityFqn: null);
    }

    private void TryExtractFluentRelationshipFromChain(
        InvocationExpressionSyntax invocation,
        string? ownerEntityFqn)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
            return;

        var methodName = methodSymbol.Name;

        // Detect HasOne<T>, HasMany<T>
        if (methodName is "HasOne" or "HasMany")
        {
            var relationType = methodName == "HasOne" ? "HasOne" : "HasMany";
            var targetEntity = ResolveTypeArgumentFromInvocation(methodSymbol);

            if (targetEntity is null)
                return;

            var targetFqn = GetFqn(targetEntity);
            var fromFqn = ownerEntityFqn ?? TryResolveEntityFromChain(invocation);

            if (fromFqn is null)
                return;

            // Look ahead in the chain for HasForeignKey
            var foreignKey = TryExtractForeignKey(invocation);

            _entityRelationships.Add(new EntityRelationship(
                fromFqn,
                targetFqn,
                relationType,
                foreignKey
            ));
        }

        // Detect HasMany().WithMany() for many-to-many
        if (methodName == "WithMany" && ownerEntityFqn is not null)
        {
            // The parent invocation should be HasMany — already captured above.
            // Mark it as ManyToMany by finding the existing relationship and updating.
            // Best-effort: emit a separate ManyToMany record if we can resolve both sides.
            var targetEntity = ResolveTypeArgumentFromInvocation(methodSymbol);
            if (targetEntity is not null)
            {
                var targetFqn = GetFqn(targetEntity);
                _entityRelationships.Add(new EntityRelationship(
                    ownerEntityFqn,
                    targetFqn,
                    "ManyToMany",
                    ForeignKeyProperty: null
                ));
            }
        }
    }

    private static ITypeSymbol? ResolveTypeArgumentFromInvocation(IMethodSymbol methodSymbol)
    {
        // Generic methods like HasOne<T>() or HasMany<T>()
        if (methodSymbol.TypeArguments.Length == 1)
            return methodSymbol.TypeArguments[0];

        // Non-generic overloads: HasOne(typeof(T)) — not handled (best-effort).
        // Also check return type for navigation builder patterns:
        // HasOne returns ReferenceNavigationBuilder<TEntity, TRelated> — extract TRelated.
        if (methodSymbol.ReturnType is INamedTypeSymbol returnType && returnType.TypeArguments.Length >= 2)
        {
            return returnType.TypeArguments[^1];
        }

        return null;
    }

    /// <summary>
    /// Walk up the invocation chain to find an Entity&lt;T&gt;() call and extract T.
    /// Covers patterns like: modelBuilder.Entity&lt;Order&gt;().HasMany(...)
    /// </summary>
    private string? TryResolveEntityFromChain(InvocationExpressionSyntax invocation)
    {
        var current = invocation.Expression;

        // Walk through MemberAccessExpression chains
        while (current is MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is InvocationExpressionSyntax parentInvocation)
            {
                var parentSymbol = _semanticModel.GetSymbolInfo(parentInvocation).Symbol as IMethodSymbol;
                if (parentSymbol?.Name == "Entity" && parentSymbol.TypeArguments.Length == 1)
                {
                    return GetFqn(parentSymbol.TypeArguments[0]);
                }

                current = parentInvocation.Expression;
            }
            else
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Look for a .HasForeignKey() or .HasForeignKey&lt;T&gt;(x => x.Prop) call
    /// chained after the current invocation. Best-effort: inspect sibling invocations
    /// in the same statement chain.
    /// </summary>
    private string? TryExtractForeignKey(InvocationExpressionSyntax invocation)
    {
        // Walk up to find the full chained expression, then search forward for HasForeignKey
        var statement = invocation.Ancestors()
            .OfType<ExpressionStatementSyntax>()
            .FirstOrDefault();

        if (statement is null)
            return null;

        foreach (var chainedInvocation in statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var chainedSymbol = _semanticModel.GetSymbolInfo(chainedInvocation).Symbol as IMethodSymbol;
            if (chainedSymbol?.Name != "HasForeignKey")
                continue;

            // Try to extract the property name from the lambda: x => x.PropertyName
            if (chainedInvocation.ArgumentList.Arguments.Count > 0)
            {
                var arg = chainedInvocation.ArgumentList.Arguments[0].Expression;
                if (arg is SimpleLambdaExpressionSyntax lambda &&
                    lambda.Body is MemberAccessExpressionSyntax propAccess)
                {
                    return propAccess.Name.Identifier.Text;
                }

                if (arg is ParenthesizedLambdaExpressionSyntax parenLambda &&
                    parenLambda.Body is MemberAccessExpressionSyntax parenPropAccess)
                {
                    return parenPropAccess.Name.Identifier.Text;
                }
            }

            // If no lambda argument, return null — we know HasForeignKey was called but can't resolve the property
            return null;
        }

        return null;
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static string GetFqn(ISymbol symbol) => SymbolExtractor.GetFqn(symbol);
}
