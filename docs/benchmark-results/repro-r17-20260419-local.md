## Ollama embedding benchmark — 2026-04-19 23:38Z

- Endpoint: `http://localhost:11434`
- Corpus: 2000 synthetic code-like strings, seed 42
- Repeats per scenario: 3 (median reported)
- Mode: R17 baseline reproduction

| Model | Batch | Parallel | Total texts | Median wall | Throughput (texts/s) | Errors |
|-------|------:|---------:|------------:|------------:|---------------------:|-------:|
| nomic-embed-text | 50 | 1 | 50 | 2.41s | 20.7 | 0 |
| nomic-embed-text | 200 | 1 | 200 | 9.41s | 21.3 | 0 |
| nomic-embed-text | 50 | 4 | 200 | 9.38s | 21.3 | 0 |
