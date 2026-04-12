using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

public sealed class MiddlewarePipelineTests
{
    [Fact]
    public void ExtractsMiddlewareOrder()
    {
        var result = AnalyzeMiddleware("""
            namespace MyApp;
            public class Startup
            {
                public void Configure(object app)
                {
                    app.UseHttpsRedirection();
                    app.UseAuthentication();
                    app.UseAuthorization();
                }
            }
            """);

        Assert.Equal(3, result.Middlewares.Count);
        Assert.Equal("UseHttpsRedirection", result.Middlewares[0].Name);
        Assert.Equal("UseAuthentication", result.Middlewares[1].Name);
        Assert.Equal("UseAuthorization", result.Middlewares[2].Name);
        Assert.Equal(0, result.Middlewares[0].Order);
        Assert.Equal(1, result.Middlewares[1].Order);
        Assert.Equal(2, result.Middlewares[2].Order);
    }

    [Fact]
    public void CreatesPipelineOrderEdges()
    {
        var result = AnalyzeMiddleware("""
            namespace MyApp;
            public class Startup
            {
                public void Configure(object app)
                {
                    app.UseRouting();
                    app.UseCors();
                    app.UseEndpoints();
                }
            }
            """);

        Assert.Equal(2, result.Relationships.Count);
        Assert.Contains(result.Relationships, r =>
            r.Type == RelationshipType.PipelineOrder &&
            r.FromFqn == "middleware:UseRouting" &&
            r.ToFqn == "middleware:UseCors");
        Assert.Contains(result.Relationships, r =>
            r.Type == RelationshipType.PipelineOrder &&
            r.FromFqn == "middleware:UseCors" &&
            r.ToFqn == "middleware:UseEndpoints");
    }

    [Fact]
    public void NoMiddleware_EmptyResult()
    {
        var result = AnalyzeMiddleware("""
            namespace MyApp;
            public class Service
            {
                public void DoWork() { }
            }
            """);

        Assert.Empty(result.Middlewares);
        Assert.Empty(result.Relationships);
    }

    // === R20 Issue #4: filter false positives (EF ModelBuilder.UseXxx, DbContextOptionsBuilder.UseXxx) ===

    /// <summary>
    /// User smoke test reported that get_middleware_pipeline on Program.cs was catching
    /// <c>UseIdentityByDefaultColumns</c> (EF migration ModelBuilder extension) and
    /// <c>UseNpgsql</c> (DbContextOptionsBuilder extension) as middleware. Both match
    /// the syntactic "UseXxx" pattern but are NOT ASP.NET middleware. The semantic
    /// filter should exclude them when the receiver type doesn't implement
    /// <c>IApplicationBuilder</c>.
    ///
    /// This test provides stub definitions of both ASP.NET and EF types so the
    /// semantic model can resolve the receiver types correctly.
    /// </summary>
    [Fact]
    public void FalsePositives_EfBuilderCalls_AreFiltered()
    {
        var code = """
            namespace Microsoft.AspNetCore.Builder
            {
                public interface IApplicationBuilder { }
                public static class AppExtensions
                {
                    public static IApplicationBuilder UseAuthentication(this IApplicationBuilder b) => b;
                    public static IApplicationBuilder UseAuthorization(this IApplicationBuilder b) => b;
                    public static IApplicationBuilder UseRouting(this IApplicationBuilder b) => b;
                }
            }

            namespace Microsoft.EntityFrameworkCore
            {
                public class DbContextOptionsBuilder { }
                public static class NpgsqlExtensions
                {
                    public static DbContextOptionsBuilder UseNpgsql(this DbContextOptionsBuilder o, string cs) => o;
                }

                public class ModelBuilder { }
                public static class ModelBuilderExtensions
                {
                    public static ModelBuilder UseIdentityByDefaultColumns(this ModelBuilder mb) => mb;
                    public static ModelBuilder UseSerialColumns(this ModelBuilder mb) => mb;
                }
            }

            namespace MyApp
            {
                using Microsoft.AspNetCore.Builder;
                using Microsoft.EntityFrameworkCore;

                public class Program
                {
                    public static void ConfigurePipeline(IApplicationBuilder app)
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                    }

                    public static void ConfigureDb(DbContextOptionsBuilder options)
                    {
                        options.UseNpgsql("Host=localhost");
                    }

                    public static void ConfigureModel(ModelBuilder mb)
                    {
                        mb.UseIdentityByDefaultColumns();
                        mb.UseSerialColumns();
                    }
                }
            }
            """;

        var result = AnalyzeMiddleware(code);

        // Only the 3 real middleware calls should be picked up.
        Assert.Equal(3, result.Middlewares.Count);
        Assert.Contains(result.Middlewares, m => m.Name == "UseRouting");
        Assert.Contains(result.Middlewares, m => m.Name == "UseAuthentication");
        Assert.Contains(result.Middlewares, m => m.Name == "UseAuthorization");

        // The EF/Npgsql calls must NOT be in the pipeline.
        Assert.DoesNotContain(result.Middlewares, m => m.Name == "UseNpgsql");
        Assert.DoesNotContain(result.Middlewares, m => m.Name == "UseIdentityByDefaultColumns");
        Assert.DoesNotContain(result.Middlewares, m => m.Name == "UseSerialColumns");
    }

    [Fact]
    public void InstanceMethod_OnApplicationBuilder_IsDetected()
    {
        // Middleware can also be called via non-extension methods in rare cases.
        // The filter must handle both: extension methods (first param type check)
        // and instance methods (ContainingType check).
        var code = """
            namespace Microsoft.AspNetCore.Builder
            {
                public interface IApplicationBuilder { }
                public class ApplicationBuilder : IApplicationBuilder
                {
                    public virtual ApplicationBuilder UseCustom() => this;
                }
            }

            namespace MyApp
            {
                using Microsoft.AspNetCore.Builder;
                public class Program
                {
                    public static void Configure(ApplicationBuilder app)
                    {
                        app.UseCustom();
                    }
                }
            }
            """;

        var result = AnalyzeMiddleware(code);

        Assert.Single(result.Middlewares);
        Assert.Equal("UseCustom", result.Middlewares[0].Name);
    }

    [Fact]
    public void UnresolvedSemantics_FallsBackToSyntacticMatch()
    {
        // Existing tests with `object app` have no semantic info (object doesn't have
        // UseXxx). The fallback must keep working so we don't break hand-written
        // test snippets or partial compilations.
        var result = AnalyzeMiddleware("""
            namespace MyApp;
            public class Startup
            {
                public void Configure(object app)
                {
                    app.UseRouting();
                    app.UseAuthentication();
                }
            }
            """);

        // With `object` receiver the semantic model returns null Symbol,
        // so we fall back to the syntactic "UseXxx" match.
        Assert.Equal(2, result.Middlewares.Count);
    }

    private static MiddlewarePipelineResult AnalyzeMiddleware(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(tree);
        var analyzer = new MiddlewarePipelineAnalyzer(semanticModel);
        return analyzer.Analyze(tree.GetRoot());
    }
}
