## Ollama embedding benchmark — 2026-04-19 15:49Z

- Endpoint: `http://192.168.50.14:11434`
- Corpus: 2000 synthetic code-like strings, seed 42
- Repeats per scenario: 3 (median reported)
- Mode: R17 baseline reproduction

| Model | Batch | Parallel | Total texts | Median wall | Throughput (texts/s) | Errors |
|-------|------:|---------:|------------:|------------:|---------------------:|-------:|
| nomic-embed-text | 50 | 1 | 50 | 10.98s | 4.6 | 0 |
| nomic-embed-text | 200 | 1 | 200 | 43.52s | 4.6 | 0 |
| nomic-embed-text | 50 | 4 | 200 | 43.57s | 4.6 | 0 |
