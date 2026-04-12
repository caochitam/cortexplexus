using System.Diagnostics;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using CortexPlexus.Parsing.Roslyn;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace CortexPlexus.Parsing;

internal sealed class RoslynCodeParser : ICodeParser
{
    private static bool _msbuildRegistered;
    private static readonly object _registrationLock = new();

    private readonly ILogger<RoslynCodeParser> _logger;

    /// <summary>
    /// Files trong các thư mục build/generated phải bị skip — không phải user code.
    /// SDK-style csproj implicit include cả `obj/Debug/net*/*.cs` (source generators output,
    /// AssemblyAttributes.cs, GlobalUsings.g.cs) → những file này tạo ra rất nhiều symbols
    /// noise và làm graph store bị bloat (CortexFlow R12: ~30% symbols là từ obj/).
    /// </summary>
    internal static bool IsBuildArtifactPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var normalized = filePath.Replace('\\', '/');
        return normalized.Contains("/bin/")
            || normalized.Contains("/obj/")
            || normalized.Contains("/node_modules/")
            || normalized.Contains("/.git/");
    }

    public RoslynCodeParser(ILogger<RoslynCodeParser> logger)
    {
        _logger = logger;
    }

    public async Task<ParseResult> ParseSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        EnsureMSBuildRegistered();

        var stopwatch = Stopwatch.StartNew();
        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();
        var filesProcessed = 0;
        var errorCount = 0;

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            _logger.LogWarning("Workspace warning: {Diagnostic}", e.Diagnostic.Message);
        };

        _logger.LogInformation("Opening: {Path}", solutionPath);

        IEnumerable<Project> projects;
        if (solutionPath.EndsWith(".sln") || solutionPath.EndsWith(".slnx"))
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
            projects = solution.Projects;
        }
        else
        {
            // .csproj — open as single project
            var project = await workspace.OpenProjectAsync(solutionPath, cancellationToken: ct);
            projects = [project];
        }

        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation("Processing project: {ProjectName}", project.Name);

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                _logger.LogWarning("Failed to get compilation for project: {ProjectName}", project.Name);
                errorCount++;
                continue;
            }

            LogCompilationDiagnostics(compilation, project.Name);

            var skippedBuildArtifacts = 0;
            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();

                // Skip build artifacts (obj/Debug/net*/*.cs, bin/, node_modules/) — these are
                // SDK-generated source files (AssemblyAttributes, GlobalUsings, source generators)
                // không phải user code. Bỏ qua giúp giảm 20-40% symbols cho large SDK-style projects.
                if (IsBuildArtifactPath(tree.FilePath))
                {
                    skippedBuildArtifacts++;
                    continue;
                }

                try
                {
                    var semanticModel = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync(ct);

                    var symbolExtractor = new SymbolExtractor(semanticModel);
                    symbolExtractor.Visit(root);
                    allSymbols.AddRange(symbolExtractor.Symbols);
                    allRelationships.AddRange(symbolExtractor.Relationships);

                    var callGraphExtractor = new CallGraphExtractor(semanticModel);
                    callGraphExtractor.Visit(root);
                    allRelationships.AddRange(callGraphExtractor.Relationships);

                    var typeDependencyExtractor = new TypeDependencyExtractor(semanticModel);
                    typeDependencyExtractor.Visit(root);
                    allRelationships.AddRange(typeDependencyExtractor.Relationships);

                    // Phase 3: .NET Deep Analysis
                    var efAnalyzer = new EfCoreAnalyzer(semanticModel, compilation);
                    var efResult = efAnalyzer.Analyze(tree);
                    allSymbols.AddRange(efResult.DbContexts);
                    allRelationships.AddRange(efResult.Relationships);

                    var diAnalyzer = new DiContainerAnalyzer(semanticModel);
                    var diResult = diAnalyzer.Analyze(root);
                    allSymbols.AddRange(diResult.Registrations);

                    var routeAnalyzer = new AspNetRouteAnalyzer(semanticModel);
                    var routeResult = routeAnalyzer.Analyze(root);
                    allSymbols.AddRange(routeResult.Endpoints);
                    allRelationships.AddRange(routeResult.Relationships);

                    // P1b: Configuration Mapping
                    var configAnalyzer = new ConfigurationAnalyzer(semanticModel);
                    var configResult = configAnalyzer.Analyze(tree);
                    allRelationships.AddRange(configResult.Relationships);

                    // P2b: Exception Flow
                    var exceptionExtractor = new ExceptionFlowExtractor(semanticModel);
                    exceptionExtractor.Visit(root);
                    allRelationships.AddRange(exceptionExtractor.Relationships);

                    // P2e: HTTP Call Tracing
                    var httpExtractor = new HttpCallExtractor(semanticModel);
                    httpExtractor.Visit(root);
                    allRelationships.AddRange(httpExtractor.Relationships);

                    // P3a: Event/Messaging Patterns (MediatR, domain events, delegates)
                    var eventPatternExtractor = new EventPatternExtractor(semanticModel);
                    eventPatternExtractor.Visit(root);
                    allRelationships.AddRange(eventPatternExtractor.Relationships);

                    // P4a: Middleware Pipeline Order
                    var middlewareAnalyzer = new MiddlewarePipelineAnalyzer(semanticModel);
                    var middlewareResult = middlewareAnalyzer.Analyze(root);
                    allSymbols.AddRange(middlewareResult.Middlewares);
                    allRelationships.AddRange(middlewareResult.Relationships);

                    // P4b: API Contract Mapping (endpoint → request/response DTOs)
                    var contractAnalyzer = new ApiContractAnalyzer(semanticModel);
                    var contractResult = contractAnalyzer.Analyze(root);
                    allRelationships.AddRange(contractResult.Relationships);

                    filesProcessed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Error processing syntax tree: {FilePath}", tree.FilePath);
                    errorCount++;
                }
            }

            if (skippedBuildArtifacts > 0)
                _logger.LogInformation("Skipped {Count} build artifact files (bin/obj/) in project {ProjectName}",
                    skippedBuildArtifacts, project.Name);
        }

        // P1b: Parse config files in solution directory
        var solutionDir = Path.GetDirectoryName(solutionPath);
        if (solutionDir is not null)
            allSymbols.AddRange(ConfigFileParser.ParseConfigFiles(solutionDir));

        stopwatch.Stop();

        _logger.LogInformation(
            "Parsed solution in {Duration}ms — {Symbols} symbols, {Relationships} relationships, {Files} files, {Errors} errors",
            stopwatch.ElapsedMilliseconds, allSymbols.Count, allRelationships.Count, filesProcessed, errorCount);

        return new ParseResult(allSymbols, allRelationships, stopwatch.Elapsed, filesProcessed, errorCount);
    }

    public async Task<ParseResult> ParseFilesAsync(
        IEnumerable<string> filePaths, string projectPath, CancellationToken ct = default)
    {
        EnsureMSBuildRegistered();

        var stopwatch = Stopwatch.StartNew();
        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();
        var filesProcessed = 0;
        var errorCount = 0;

        var fileSet = filePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            _logger.LogWarning("Workspace warning: {Diagnostic}", e.Diagnostic.Message);
        };

        _logger.LogInformation("Opening: {ProjectPath} for incremental parse of {FileCount} files",
            projectPath, fileSet.Count);

        // Open as solution (.sln/.slnx) or project (.csproj) — same logic as ParseSolutionAsync
        IEnumerable<Project> projects;
        if (projectPath.EndsWith(".sln") || projectPath.EndsWith(".slnx"))
        {
            var solution = await workspace.OpenSolutionAsync(projectPath, cancellationToken: ct);
            projects = solution.Projects;
        }
        else
        {
            var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
            projects = [project];
        }

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                _logger.LogWarning("Failed to get compilation for project: {ProjectName}", project.Name);
                errorCount++;
                continue;
            }

            LogCompilationDiagnostics(compilation, project.Name);

            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();

                if (!fileSet.Contains(tree.FilePath))
                    continue;

                // Skip build artifacts (cùng filter với ParseSolutionAsync)
                if (IsBuildArtifactPath(tree.FilePath))
                    continue;

                try
                {
                    var semanticModel = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync(ct);

                    var symbolExtractor = new SymbolExtractor(semanticModel);
                    symbolExtractor.Visit(root);
                    allSymbols.AddRange(symbolExtractor.Symbols);
                    allRelationships.AddRange(symbolExtractor.Relationships);

                    var callGraphExtractor = new CallGraphExtractor(semanticModel);
                    callGraphExtractor.Visit(root);
                    allRelationships.AddRange(callGraphExtractor.Relationships);

                    var typeDependencyExtractor = new TypeDependencyExtractor(semanticModel);
                    typeDependencyExtractor.Visit(root);
                    allRelationships.AddRange(typeDependencyExtractor.Relationships);

                    // Phase 3: .NET Deep Analysis (same as full parse)
                    var efAnalyzer = new EfCoreAnalyzer(semanticModel, compilation);
                    var efResult = efAnalyzer.Analyze(tree);
                    allSymbols.AddRange(efResult.DbContexts);
                    allRelationships.AddRange(efResult.Relationships);

                    var diAnalyzer = new DiContainerAnalyzer(semanticModel);
                    var diResult = diAnalyzer.Analyze(root);
                    allSymbols.AddRange(diResult.Registrations);

                    var routeAnalyzer = new AspNetRouteAnalyzer(semanticModel);
                    var routeResult = routeAnalyzer.Analyze(root);
                    allSymbols.AddRange(routeResult.Endpoints);
                    allRelationships.AddRange(routeResult.Relationships);

                    // P1b: Configuration Mapping
                    var configAnalyzer = new ConfigurationAnalyzer(semanticModel);
                    var configResult = configAnalyzer.Analyze(tree);
                    allRelationships.AddRange(configResult.Relationships);

                    // P2b: Exception Flow
                    var exceptionExtractor = new ExceptionFlowExtractor(semanticModel);
                    exceptionExtractor.Visit(root);
                    allRelationships.AddRange(exceptionExtractor.Relationships);

                    // P2e: HTTP Call Tracing
                    var httpExtractor = new HttpCallExtractor(semanticModel);
                    httpExtractor.Visit(root);
                    allRelationships.AddRange(httpExtractor.Relationships);

                    // P3a: Event/Messaging Patterns (MediatR, domain events, delegates)
                    var eventPatternExtractor = new EventPatternExtractor(semanticModel);
                    eventPatternExtractor.Visit(root);
                    allRelationships.AddRange(eventPatternExtractor.Relationships);

                    // P4a: Middleware Pipeline Order
                    var middlewareAnalyzer = new MiddlewarePipelineAnalyzer(semanticModel);
                    var middlewareResult = middlewareAnalyzer.Analyze(root);
                    allSymbols.AddRange(middlewareResult.Middlewares);
                    allRelationships.AddRange(middlewareResult.Relationships);

                    // P4b: API Contract Mapping (endpoint → request/response DTOs)
                    var contractAnalyzer = new ApiContractAnalyzer(semanticModel);
                    var contractResult = contractAnalyzer.Analyze(root);
                    allRelationships.AddRange(contractResult.Relationships);

                    filesProcessed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Error processing syntax tree: {FilePath}", tree.FilePath);
                    errorCount++;
                }
            }
        }

        stopwatch.Stop();

        _logger.LogInformation(
            "Parsed {Files} files in {Duration}ms — {Symbols} symbols, {Relationships} relationships, {Errors} errors",
            filesProcessed, stopwatch.ElapsedMilliseconds, allSymbols.Count, allRelationships.Count, errorCount);

        return new ParseResult(allSymbols, allRelationships, stopwatch.Elapsed, filesProcessed, errorCount);
    }

    private void LogCompilationDiagnostics(Compilation compilation, string projectName)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            _logger.LogWarning(
                "Project {ProjectName} has {ErrorCount} compilation errors (parsing will continue with partial results)",
                projectName, errors.Count);

            foreach (var error in errors.Take(10))
            {
                _logger.LogDebug("  {Id}: {Message}", error.Id, error.GetMessage());
            }

            if (errors.Count > 10)
            {
                _logger.LogDebug("  ... and {Remaining} more errors", errors.Count - 10);
            }
        }
    }

    private static void EnsureMSBuildRegistered()
    {
        if (_msbuildRegistered)
            return;

        lock (_registrationLock)
        {
            if (_msbuildRegistered)
                return;

            // Try SDK first (dev environment), fall back to shipped assemblies (Docker/production)
            if (MSBuildLocator.CanRegister)
            {
                try
                {
                    if (MSBuildLocator.QueryVisualStudioInstances().Any())
                    {
                        MSBuildLocator.RegisterDefaults();
                    }
                    else
                    {
                        // No SDK found — use Microsoft.Build assemblies shipped with the app
                        MSBuildLocator.RegisterMSBuildPath(AppContext.BaseDirectory);
                    }
                }
                catch
                {
                    MSBuildLocator.RegisterMSBuildPath(AppContext.BaseDirectory);
                }
            }

            _msbuildRegistered = true;
        }
    }
}
