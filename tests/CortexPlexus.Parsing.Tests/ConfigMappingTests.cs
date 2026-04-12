using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.TreeSitter;

namespace CortexPlexus.Parsing.Tests;

// ============================================================
// ConfigAccessDetector — Cross-language env var detection
// ============================================================

public sealed class ConfigAccessDetectorPythonTests
{
    [Fact]
    public void DetectsOsEnvironSubscript()
    {
        var rels = ParsePythonConfig("""
            import os
            def connect():
                url = os.environ["DATABASE_URL"]
            """, "mymodule.connect");

        Assert.Contains(rels, r => r.ToFqn == "env:DATABASE_URL" && r.Type == RelationshipType.ReadsConfig);
    }

    [Fact]
    public void DetectsOsEnvironGet()
    {
        var rels = ParsePythonConfig("""
            import os
            def get_key():
                return os.environ.get("SECRET_KEY")
            """, "mymodule.get_key");

        Assert.Contains(rels, r => r.ToFqn == "env:SECRET_KEY" && r.Type == RelationshipType.ReadsConfig);
    }

    [Fact]
    public void DetectsOsGetenv()
    {
        var rels = ParsePythonConfig("""
            import os
            def load():
                return os.getenv("API_TOKEN")
            """, "mymodule.load");

        Assert.Contains(rels, r => r.ToFqn == "env:API_TOKEN" && r.Type == RelationshipType.ReadsConfig);
    }

    [Fact]
    public void NoFalsePositives()
    {
        var rels = ParsePythonConfig("""
            def normal():
                x = some_dict["key"]
            """, "mymodule.normal");

        Assert.Empty(rels);
    }

    private static List<Relationship> ParsePythonConfig(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("python");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return ConfigAccessDetector.DetectPython(tree.RootNode, callerFqn);
    }
}

public sealed class ConfigAccessDetectorTypeScriptTests
{
    [Fact]
    public void DetectsProcessEnvDot()
    {
        var rels = ParseTsConfig("""
            function getPort() {
                return process.env.PORT;
            }
            """, "app.getPort");

        Assert.Contains(rels, r => r.ToFqn == "env:PORT" && r.Type == RelationshipType.ReadsConfig);
    }

    [Fact]
    public void DetectsProcessEnvBracket()
    {
        var rels = ParseTsConfig("""
            function getDb() {
                return process.env["DB_HOST"];
            }
            """, "app.getDb");

        Assert.Contains(rels, r => r.ToFqn == "env:DB_HOST" && r.Type == RelationshipType.ReadsConfig);
    }

    [Fact]
    public void NoDuplicates()
    {
        var rels = ParseTsConfig("""
            function init() {
                const a = process.env.PORT;
                const b = process.env.PORT;
            }
            """, "app.init");

        var portRels = rels.Where(r => r.ToFqn == "env:PORT").ToList();
        Assert.Single(portRels);
    }

    private static List<Relationship> ParseTsConfig(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("typescript");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return ConfigAccessDetector.DetectTypeScript(tree.RootNode, callerFqn);
    }
}

public sealed class ConfigAccessDetectorJavaTests
{
    [Fact]
    public void DetectsSystemGetenv()
    {
        var rels = ParseJavaConfig("""
            class Config {
                String get() {
                    return System.getenv("JAVA_HOME");
                }
            }
            """, "Config.get");

        Assert.Contains(rels, r => r.ToFqn == "env:JAVA_HOME" && r.Type == RelationshipType.ReadsConfig);
    }

    [Fact]
    public void DetectsSystemGetProperty()
    {
        var rels = ParseJavaConfig("""
            class Config {
                String get() {
                    return System.getProperty("user.home");
                }
            }
            """, "Config.get");

        Assert.Contains(rels, r => r.ToFqn == "env:user.home" && r.Type == RelationshipType.ReadsConfig);
    }

    private static List<Relationship> ParseJavaConfig(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("java");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return ConfigAccessDetector.DetectJava(tree.RootNode, callerFqn);
    }
}

public sealed class ConfigAccessDetectorGoTests
{
    [Fact]
    public void DetectsOsGetenv()
    {
        var rels = ParseGoConfig("""
            package main
            import "os"
            func getDB() string {
                return os.Getenv("DATABASE_URL")
            }
            """, "main.getDB");

        Assert.Contains(rels, r => r.ToFqn == "env:DATABASE_URL" && r.Type == RelationshipType.ReadsConfig);
    }

    private static List<Relationship> ParseGoConfig(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("go");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return ConfigAccessDetector.DetectGo(tree.RootNode, callerFqn);
    }
}

public sealed class ConfigAccessDetectorRustTests
{
    [Fact]
    public void DetectsEnvVar()
    {
        var rels = ParseRustConfig("""
            use std::env;
            fn get_key() -> String {
                env::var("API_KEY").unwrap()
            }
            """, "crate::get_key");

        Assert.Contains(rels, r => r.ToFqn == "env:API_KEY" && r.Type == RelationshipType.ReadsConfig);
    }

    private static List<Relationship> ParseRustConfig(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("rust");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return ConfigAccessDetector.DetectRust(tree.RootNode, callerFqn);
    }
}

public sealed class ConfigAccessDetectorPhpTests
{
    [Fact]
    public void DetectsGetenv()
    {
        var rels = ParsePhpConfig("""
            <?php
            function getDb() {
                return getenv("DB_HOST");
            }
            """, "App\\getDb");

        Assert.Contains(rels, r => r.ToFqn == "env:DB_HOST" && r.Type == RelationshipType.ReadsConfig);
    }

    [Fact]
    public void DetectsEnvSuperglobal()
    {
        var rels = ParsePhpConfig("""
            <?php
            function getKey() {
                return $_ENV["SECRET_KEY"];
            }
            """, "App\\getKey");

        Assert.Contains(rels, r => r.ToFqn == "env:SECRET_KEY" && r.Type == RelationshipType.ReadsConfig);
    }

    private static List<Relationship> ParsePhpConfig(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("php");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return ConfigAccessDetector.DetectPhp(tree.RootNode, callerFqn);
    }
}

// ============================================================
// ConfigFileParser — appsettings.json, .env, docker-compose.yml
// ============================================================

public sealed class ConfigFileParserTests
{
    [Fact]
    public void ParsesAppSettingsJson()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "appsettings.json"), """
            {
                "ConnectionStrings": {
                    "Default": "Host=localhost"
                },
                "Logging": {
                    "LogLevel": {
                        "Default": "Information"
                    }
                }
            }
            """);

        var symbols = ConfigFileParser.ParseConfigFiles(dir.Path);

        Assert.Contains(symbols, s => s.Fqn == "config:ConnectionStrings:Default");
        Assert.Contains(symbols, s => s.Fqn == "config:Logging:LogLevel:Default");
        Assert.All(symbols, s => Assert.Equal("config_key", s.Kind));
    }

    [Fact]
    public void ParsesDotEnv()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".env"), """
            # Database config
            DATABASE_URL=postgres://localhost/mydb
            SECRET_KEY=abc123
            """);

        var symbols = ConfigFileParser.ParseConfigFiles(dir.Path);

        Assert.Contains(symbols, s => s.Fqn == "env:DATABASE_URL");
        Assert.Contains(symbols, s => s.Fqn == "env:SECRET_KEY");
        Assert.DoesNotContain(symbols, s => s.Name.StartsWith("#"));
    }

    [Fact]
    public void ParsesDockerComposeEnvironment()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "docker-compose.yml"), """
            services:
              app:
                image: myapp
                environment:
                  - POSTGRES_HOST=db
                  - POSTGRES_PORT=5432
              db:
                image: postgres
            """);

        var symbols = ConfigFileParser.ParseConfigFiles(dir.Path);

        Assert.Contains(symbols, s => s.Fqn == "env:POSTGRES_HOST");
        Assert.Contains(symbols, s => s.Fqn == "env:POSTGRES_PORT");
    }

    [Fact]
    public void SkipsMalformedJson()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "appsettings.json"), "{ invalid json");

        var symbols = ConfigFileParser.ParseConfigFiles(dir.Path);
        Assert.Empty(symbols);
    }

    [Fact]
    public void SkipsEmptyDirectory()
    {
        var symbols = ConfigFileParser.ParseConfigFiles(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Empty(symbols);
    }

    // === R20 Issue #1: recursive scan for multi-project solutions ===

    /// <summary>
    /// User smoke test reported get_config_usage("Qdrant") empty on CortexFlow.
    /// Root cause: CortexFlow has appsettings.json at 03.Backend/CortexFlow.API/
    /// (2 levels deep), but ParseConfigFiles was non-recursive and only scanned
    /// the solution root. After R20: recursive walk picks up nested configs.
    /// </summary>
    [Fact]
    public void RecursiveScan_FindsConfigFilesInSubdirectories()
    {
        using var dir = new TempDir();

        // Mimic a multi-project .NET solution layout
        var backend = Path.Combine(dir.Path, "03.Backend", "MyApi");
        Directory.CreateDirectory(backend);
        File.WriteAllText(Path.Combine(backend, "appsettings.json"), """
            {
              "ConnectionStrings": {
                "Qdrant": "http://localhost:6333"
              },
              "AI": {
                "GeminiApiKey": "replace-me"
              }
            }
            """);

        var frontend = Path.Combine(dir.Path, "04.Frontend", "MyApp", "wwwroot");
        Directory.CreateDirectory(frontend);
        File.WriteAllText(Path.Combine(frontend, "appsettings.Development.json"), """
            {
              "ApiBaseUrl": "http://localhost:5000"
            }
            """);

        var symbols = ConfigFileParser.ParseConfigFiles(dir.Path);

        Assert.Contains(symbols, s => s.Fqn == "config:ConnectionStrings:Qdrant");
        Assert.Contains(symbols, s => s.Fqn == "config:AI:GeminiApiKey");
        Assert.Contains(symbols, s => s.Fqn == "config:ApiBaseUrl");
    }

    [Fact]
    public void RecursiveScan_SkipsBuildArtifactDirectories()
    {
        using var dir = new TempDir();

        // A config file under bin/ must not be picked up — it's an artifact.
        var bin = Path.Combine(dir.Path, "src", "MyApi", "bin", "Debug");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "appsettings.json"), """
            {
              "ShouldNotAppear": "in results"
            }
            """);

        // A real config outside bin/
        var real = Path.Combine(dir.Path, "src", "MyApi");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "appsettings.json"), """
            {
              "RealKey": "value"
            }
            """);

        var symbols = ConfigFileParser.ParseConfigFiles(dir.Path);

        Assert.Contains(symbols, s => s.Fqn == "config:RealKey");
        Assert.DoesNotContain(symbols, s => s.Fqn == "config:ShouldNotAppear");
    }

    [Fact]
    public void RecursiveScan_SkipsNodeModulesAndGit()
    {
        using var dir = new TempDir();

        var nodeModules = Path.Combine(dir.Path, "node_modules", "some-pkg");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllText(Path.Combine(nodeModules, ".env"), "NODE_MODULES_KEY=should_not_appear");

        var git = Path.Combine(dir.Path, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, ".env"), "GIT_KEY=should_not_appear");

        // A legitimate .env at the root
        File.WriteAllText(Path.Combine(dir.Path, ".env"), "REAL_KEY=keep_me");

        var symbols = ConfigFileParser.ParseConfigFiles(dir.Path);

        Assert.Contains(symbols, s => s.Fqn == "env:REAL_KEY");
        Assert.DoesNotContain(symbols, s => s.Fqn == "env:NODE_MODULES_KEY");
        Assert.DoesNotContain(symbols, s => s.Fqn == "env:GIT_KEY");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cpx_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
