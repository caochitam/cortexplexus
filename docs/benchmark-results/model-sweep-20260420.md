## Ollama embedding benchmark — 2026-04-19 23:48Z

- Endpoint: `http://localhost:11434`
- Corpus: 500 synthetic code-like strings, seed 42
- Repeats per scenario: 3 (median reported)
- Mode: full sweep

| Model | Batch | Parallel | Total texts | Median wall | Throughput (texts/s) | Errors |
|-------|------:|---------:|------------:|------------:|---------------------:|-------:|
| nomic-embed-text | 100 | 1 | 500 | 23.88s | 20.9 | 0 |
| mxbai-embed-large | 100 | 1 | 500 | 90.05s | 5.6 | 0 |
| all-minilm | 100 | 1 | 500 | 4.91s | 101.9 | 0 |
| snowflake-arctic-embed:s | 100 | 1 | 500 | 8.90s | 56.2 | 0 |
