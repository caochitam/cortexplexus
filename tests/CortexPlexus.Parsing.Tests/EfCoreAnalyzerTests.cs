using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

/// <summary>
/// Tests cho EfCoreAnalyzer — DbContext + DbSet detection.
///
/// Bug context (from CortexFlow smoke test):
/// AppDbContext có 30+ DbSet properties nhưng QueryEntityMappingsAsync trả empty.
/// Root cause: IsDbSetType() check `fqn == "Microsoft.EntityFrameworkCore.DbSet&lt;T&gt;"`
/// quá strict — fail khi compilation has errors hoặc generic param name khác T.
///
/// Fix: name + arity + namespace check.
/// </summary>
public sealed class EfCoreAnalyzerTests
{
    [Fact]
    public void StandardDbContext_EmitsMapsToEdgesForDbSets()
    {
        // Mục đích: Standard DbSet pattern phải emit MapsTo edges từ context → entity.
        var (dbContexts, _, relationships) = AnalyzeCode("""
            namespace Microsoft.EntityFrameworkCore
            {
                public class DbContext { }
                public class DbSet<T> { }
            }

            namespace MyApp.Domain
            {
                public class User { }
                public class Order { }
            }

            namespace MyApp.Data
            {
                using Microsoft.EntityFrameworkCore;
                using MyApp.Domain;

                public class AppDbContext : DbContext
                {
                    public DbSet<User> Users { get; set; }
                    public DbSet<Order> Orders { get; set; }
                }
            }
            """);

        Assert.Single(dbContexts);
        Assert.Equal("AppDbContext", dbContexts[0].Name);

        // 2 MapsTo edges expected
        var mapsToEdges = relationships.Where(r => r.Type == RelationshipType.MapsTo).ToList();
        Assert.Equal(2, mapsToEdges.Count);
        Assert.Contains(mapsToEdges, e => e.ToFqn.EndsWith("User"));
        Assert.Contains(mapsToEdges, e => e.ToFqn.EndsWith("Order"));
    }

    [Fact]
    public void DbSet_WithCustomGenericParameterName_StillDetected()
    {
        // Mục đích: DbSet<TEntity> (custom param name) phải detect — Issue #5 root cause.
        // Trước fix: IsDbSetType match `DbSet<T>` literal → fail với TEntity.
        var (dbContexts, _, relationships) = AnalyzeCode("""
            namespace Microsoft.EntityFrameworkCore
            {
                public class DbContext { }
                public class DbSet<TEntity> { }  // custom generic param name
            }

            namespace MyApp.Domain { public class Product { } }

            namespace MyApp.Data
            {
                using Microsoft.EntityFrameworkCore;
                using MyApp.Domain;
                public class ShopContext : DbContext
                {
                    public DbSet<Product> Products { get; set; }
                }
            }
            """);

        Assert.Single(dbContexts);
        var mapsToEdges = relationships.Where(r => r.Type == RelationshipType.MapsTo).ToList();
        Assert.Single(mapsToEdges);
        Assert.Contains("Product", mapsToEdges[0].ToFqn);
    }

    [Fact]
    public void NonDbContextClass_NoEdgesEmitted()
    {
        // Edge case: regular class với DbSet-like name không nên match.
        var (dbContexts, _, relationships) = AnalyzeCode("""
            namespace Other
            {
                public class MyDbSet<T> { }  // KHÔNG phải EF DbSet
            }

            namespace MyApp
            {
                public class FakeContext
                {
                    public Other.MyDbSet<string> Items { get; set; }
                }
            }
            """);

        Assert.Empty(dbContexts);
        Assert.DoesNotContain(relationships, r => r.Type == RelationshipType.MapsTo);
    }

    // --- Helper ---

    private static (
        IReadOnlyList<DbContextInfo> DbContexts,
        IReadOnlyList<EntityRelationship> EntityRelationships,
        IReadOnlyList<Relationship> Relationships
    ) AnalyzeCode(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("Test")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(tree);

        var semanticModel = compilation.GetSemanticModel(tree);
        var analyzer = new EfCoreAnalyzer(semanticModel, compilation);
        var result = analyzer.Analyze(tree);

        return (result.DbContexts, result.EntityRelationships, result.Relationships);
    }
}
