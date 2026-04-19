using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CortexPlexus.Embedding.Benchmarks;

// Harness purpose (see docs/PLAN-v0.9.0-embedding-throughput.md):
// - Reproduce R17 baseline scenarios first (sanity check).
// - Then sweep the full matrix: models × batch sizes × parallelism.
// - Emit a markdown table so the result can drop straight into docs/BENCHMARK.md.
//
// This is NOT a unit test — it talks to a real Ollama. Do not invoke from `dotnet test`.

internal static class Program
{
    private const int DefaultCorpusSize = 2_000;
    private const int DefaultSeed = 42;
    private const int RepeatsPerScenario = 3; // median of 3

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task<int> Main(string[] args)
    {
        var cfg = Config.Parse(args);
        if (cfg is null)
        {
            PrintUsage();
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        if (!await ProbeOllama(http, cfg.OllamaUrl))
            return 2;

        var corpus = BuildCorpus(cfg.CorpusSize, DefaultSeed);
        Console.Error.WriteLine($"Corpus: {corpus.Count} strings, avg {corpus.Average(s => s.Length):F0} chars, seed {DefaultSeed}");

        var scenarios = cfg.ReproR17
            ? ReproR17Scenarios(cfg.Models)
            : BuildSweep(cfg.Models, cfg.BatchSizes, cfg.Parallelism, cfg.CorpusSize);

        var results = new List<Result>();
        foreach (var scenario in scenarios)
        {
            Console.Error.WriteLine($"\n=== {scenario.Label()} ===");
            var durations = new List<double>();
            var errors = 0;

            for (var repeat = 0; repeat < RepeatsPerScenario; repeat++)
            {
                try
                {
                    var wall = await RunScenario(http, cfg.OllamaUrl, scenario, corpus);
                    durations.Add(wall.TotalSeconds);
                    Console.Error.WriteLine($"  run {repeat + 1}/{RepeatsPerScenario}: {wall.TotalSeconds:F2}s");
                }
                catch (Exception ex)
                {
                    errors++;
                    Console.Error.WriteLine($"  run {repeat + 1}/{RepeatsPerScenario}: ERROR {ex.Message}");
                }
            }

            if (durations.Count > 0)
            {
                durations.Sort();
                var median = durations[durations.Count / 2];
                var tps = scenario.TotalTexts / median;
                results.Add(new Result(scenario, median, tps, errors));
            }
            else
            {
                results.Add(new Result(scenario, double.NaN, 0, errors));
            }
        }

        var markdown = FormatMarkdown(results, cfg.OllamaUrl, cfg.ReproR17, corpus.Count);
        Console.WriteLine(markdown);
        if (cfg.OutFile is not null)
        {
            await File.WriteAllTextAsync(cfg.OutFile, markdown);
            Console.Error.WriteLine($"\nWrote report to {cfg.OutFile}");
        }

        if (cfg.ReproR17)
            AnalyzeReproR17(results);

        return 0;
    }

    // --- Config / args ---

    private sealed record Config(
        string OllamaUrl,
        string Models,
        string BatchSizes,
        string Parallelism,
        int CorpusSize,
        bool ReproR17,
        string? OutFile)
    {
        public static Config? Parse(string[] args)
        {
            string? ollama = null, models = null, batch = null, parallel = null, outFile = null;
            int corpus = DefaultCorpusSize;
            bool repro = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--ollama-url": ollama = args[++i]; break;
                    case "--models": models = args[++i]; break;
                    case "--batch-sizes": batch = args[++i]; break;
                    case "--parallelism": parallel = args[++i]; break;
                    case "--corpus-size": corpus = int.Parse(args[++i]); break;
                    case "--repro-r17": repro = true; break;
                    case "--out": outFile = args[++i]; break;
                    case "-h" or "--help": return null;
                    default:
                        Console.Error.WriteLine($"Unknown arg: {args[i]}");
                        return null;
                }
            }

            return new Config(
                OllamaUrl:   ollama   ?? "http://192.168.50.14:11434",
                Models:      models   ?? "nomic-embed-text",
                BatchSizes:  batch    ?? "50,100,200",
                Parallelism: parallel ?? "1,2,4",
                CorpusSize:  corpus,
                ReproR17:    repro,
                OutFile:     outFile);
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage: cortexplexus-embedding-bench [options]

              --ollama-url <url>     Ollama base URL (default http://192.168.50.14:11434)
              --models <csv>         Comma-sep models (default nomic-embed-text)
              --batch-sizes <csv>    Comma-sep batch sizes (default 50,100,200)
              --parallelism <csv>    Comma-sep parallelism levels (default 1,2,4)
              --corpus-size <N>      Strings to embed per run (default 2000)
              --repro-r17            Only run the 3 R17 baseline scenarios, skip sweep
              --out <file>           Write markdown table to file (stdout always gets it)
              -h, --help             Show this help

            See docs/PLAN-v0.9.0-embedding-throughput.md §3.1 for scenario matrix.
            """);
    }

    // --- Ollama probe ---

    private static async Task<bool> ProbeOllama(HttpClient http, string baseUrl)
    {
        try
        {
            using var rsp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags");
            if (rsp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Ollama reachable at {baseUrl}");
                return true;
            }
            Console.Error.WriteLine($"Ollama returned {(int)rsp.StatusCode} on /api/tags");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot reach Ollama at {baseUrl}: {ex.Message}");
            return false;
        }
    }

    // --- Scenario definition ---

    private sealed record Scenario(
        string Model,
        int BatchSize,
        int Parallelism,
        int TotalTexts)
    {
        public string Label() =>
            $"{Model} · batch={BatchSize} · parallel={Parallelism} · total={TotalTexts}";
    }

    private static List<Scenario> ReproR17Scenarios(string modelsCsv)
    {
        // Matches PLAN §3.3 and BENCHMARK.md:1544. Same model assumed.
        var model = modelsCsv.Split(',')[0].Trim();
        return
        [
            new Scenario(model, BatchSize: 50,  Parallelism: 1, TotalTexts: 50),
            new Scenario(model, BatchSize: 200, Parallelism: 1, TotalTexts: 200),
            new Scenario(model, BatchSize: 50,  Parallelism: 4, TotalTexts: 200)
        ];
    }

    private static List<Scenario> BuildSweep(string modelsCsv, string batchCsv, string parallelCsv, int totalTexts)
    {
        var models = modelsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var batchSizes = batchCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse);
        var parallelisms = parallelCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse);

        var list = new List<Scenario>();
        foreach (var m in models)
            foreach (var b in batchSizes)
                foreach (var p in parallelisms)
                    list.Add(new Scenario(m, b, p, totalTexts));
        return list;
    }

    // --- Scenario runner ---

    private static async Task<TimeSpan> RunScenario(
        HttpClient http,
        string ollamaUrl,
        Scenario scenario,
        IReadOnlyList<string> corpus)
    {
        // Split the corpus into batches of scenario.BatchSize and issue them
        // with a SemaphoreSlim-bounded concurrency of scenario.Parallelism.
        var batches = new List<List<string>>();
        for (var i = 0; i < scenario.TotalTexts; i += scenario.BatchSize)
        {
            var take = Math.Min(scenario.BatchSize, scenario.TotalTexts - i);
            batches.Add(corpus.Skip(i).Take(take).ToList());
        }

        using var gate = new SemaphoreSlim(scenario.Parallelism, scenario.Parallelism);
        var url = $"{ollamaUrl.TrimEnd('/')}/api/embed";

        var sw = Stopwatch.StartNew();
        var tasks = batches.Select(async batch =>
        {
            await gate.WaitAsync();
            try
            {
                var req = new OllamaEmbedRequest { Model = scenario.Model, Input = batch };
                using var rsp = await http.PostAsJsonAsync(url, req, JsonOpts);
                if (!rsp.IsSuccessStatusCode)
                {
                    var body = await rsp.Content.ReadAsStringAsync();
                    throw new InvalidOperationException(
                        $"Ollama returned {(int)rsp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
                }
                var parsed = await rsp.Content.ReadFromJsonAsync<OllamaEmbedResponse>(JsonOpts);
                if (parsed?.Embeddings is null || parsed.Embeddings.Count != batch.Count)
                    throw new InvalidOperationException(
                        $"Embedding count mismatch: sent {batch.Count}, got {parsed?.Embeddings?.Count ?? 0}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        sw.Stop();
        return sw.Elapsed;
    }

    // --- Corpus builder (deterministic) ---

    private static List<string> BuildCorpus(int size, int seed)
    {
        var rng = new Random(seed);
        string[] tokens =
        [
            "public", "class", "interface", "void", "async", "Task", "string", "int", "var", "return",
            "new", "await", "using", "namespace", "static", "readonly", "const", "null", "true", "false",
            "DbContext", "ILogger", "IServiceCollection", "IConfiguration", "HttpClient", "JsonSerializer",
            "CancellationToken", "IEnumerable", "List", "Dictionary", "Task.Run", "async/await",
            "Span", "ReadOnlySpan", "Memory", "ValueTask", "IAsyncEnumerable", "Nullable", "Exception",
            "try", "catch", "finally", "throw", "if", "else", "switch", "for", "foreach", "while",
            "SELECT", "FROM", "WHERE", "JOIN", "INSERT", "UPDATE", "DELETE", "BEGIN", "COMMIT", "ROLLBACK"
        ];

        var list = new List<string>(size);
        var sb = new StringBuilder(256);
        for (var i = 0; i < size; i++)
        {
            sb.Clear();
            var wordCount = 25 + rng.Next(15); // 25-40 tokens ~ 150-250 chars
            for (var w = 0; w < wordCount; w++)
            {
                if (w > 0) sb.Append(' ');
                sb.Append(tokens[rng.Next(tokens.Length)]);
            }
            list.Add(sb.ToString());
        }
        return list;
    }

    // --- Output format ---

    private static string FormatMarkdown(
        IReadOnlyList<Result> results,
        string ollamaUrl,
        bool reproMode,
        int corpusSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Ollama embedding benchmark — {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z");
        sb.AppendLine();
        sb.AppendLine($"- Endpoint: `{ollamaUrl}`");
        sb.AppendLine($"- Corpus: {corpusSize} synthetic code-like strings, seed {DefaultSeed}");
        sb.AppendLine($"- Repeats per scenario: {RepeatsPerScenario} (median reported)");
        sb.AppendLine($"- Mode: {(reproMode ? "R17 baseline reproduction" : "full sweep")}");
        sb.AppendLine();
        sb.AppendLine("| Model | Batch | Parallel | Total texts | Median wall | Throughput (texts/s) | Errors |");
        sb.AppendLine("|-------|------:|---------:|------------:|------------:|---------------------:|-------:|");
        foreach (var r in results)
        {
            var wall = double.IsNaN(r.MedianSec) ? "—" : $"{r.MedianSec:F2}s";
            var tps = double.IsNaN(r.MedianSec) ? "—" : $"{r.TextsPerSec:F1}";
            sb.AppendLine($"| {r.Scenario.Model} | {r.Scenario.BatchSize} | {r.Scenario.Parallelism} | {r.Scenario.TotalTexts} | {wall} | {tps} | {r.ErrorRuns} |");
        }
        return sb.ToString();
    }

    private static void AnalyzeReproR17(IReadOnlyList<Result> results)
    {
        // Expected from docs/BENCHMARK.md:1544-1551 (R17 ground truth, LXC).
        var expected = new[]
        {
            (Batch: 50,  Parallel: 1, Expected: 1.82, Total: 50),
            (Batch: 200, Parallel: 1, Expected: 6.85, Total: 200),
            (Batch: 50,  Parallel: 4, Expected: 6.91, Total: 200)
        };

        Console.Error.WriteLine("\n--- R17 repro comparison ---");
        foreach (var exp in expected)
        {
            var actual = results.FirstOrDefault(r =>
                r.Scenario.BatchSize == exp.Batch &&
                r.Scenario.Parallelism == exp.Parallel &&
                r.Scenario.TotalTexts == exp.Total);
            if (actual is null) continue;
            var ratio = actual.MedianSec / exp.Expected;
            var verdict = Math.Abs(ratio - 1.0) <= 0.20 ? "WITHIN 20%"
                        : ratio > 1.0 ? $"{ratio:F1}× SLOWER than R17"
                        : $"{(1 / ratio):F1}× FASTER than R17";
            Console.Error.WriteLine($"  batch={exp.Batch} parallel={exp.Parallel}: R17={exp.Expected:F2}s, now={actual.MedianSec:F2}s → {verdict}");
        }
        Console.Error.WriteLine("\nIf all three are WITHIN 20%, R17 reproduces — trust the harness, proceed to full sweep.");
        Console.Error.WriteLine("If any scenario deviates, investigate before the sweep: Ollama version, model, LXC load.");
    }

    private sealed record Result(Scenario Scenario, double MedianSec, double TextsPerSec, int ErrorRuns);

    // --- DTOs matching Ollama /api/embed wire format ---

    private sealed class OllamaEmbedRequest
    {
        public string Model { get; set; } = default!;
        public List<string> Input { get; set; } = [];
    }

    private sealed class OllamaEmbedResponse
    {
        public List<float[]?>? Embeddings { get; set; }
    }
}
