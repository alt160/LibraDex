# Agent Instructions

Use [AGENT_CODING_STYLE.md](AGENT_CODING_STYLE.md) as the coding-style reference for new C# code in this repository.

The style guide is intentionally focused on the user's personal code style: high performance patterns, low call depth/call chain, low-friction DX, compact naming, direct guards, selective comments, and visible allocation/ownership behavior.

Project-specific design decisions still belong in `LIBRADEX_DESIGN_CHECKLIST.md` or targeted restart/checkpoint artifacts. Do not treat the style guide as a substitute for current design instructions.

When choosing implementation scope, prefer controlled blast radius over minimal blast radius. First identify the best design path from correctness, performance, durability, and LibraDex DX. Keep changes as narrow as that correct design allows, but do not let narrowness outrank correctness, performance, or the core LibraDex design model. If the better path requires a heavier hand or architectural change, state that explicitly and explain the tradeoff before proceeding.

When the user replies with a short continuation phrase such as "ok. continue" or another 2-6 word response with the same meaning, end the response with the next likely action so the user can quickly decide whether another continuation response is appropriate.

## Test Artifact Lifecycle

- Treat files under `artifacts/` as disposable test output, not durable project state. Durable findings belong in tracked documentation; compact CSV, HTML, markdown, and text reports may remain under `artifacts/`.
- Give each test or benchmark run a dedicated root. Successful runs must delete generated databases, indexes, copied corpora, child work directories, and other bulky payloads after the measured result has been captured.
- A failed run may retain the smallest work directory needed for diagnosis. When the failure is corrected or superseded, delete that retained payload during the successful retry or before closing the task.
- Do not leave more than 1 GB of generated test payloads in the repository without the user's explicit approval. Before finishing any test-heavy task, measure `artifacts/` and remove disposable payloads with `Prune-TestArtifacts.ps1` or an equally narrow cleanup.
- Prefer `%TEMP%`, `T:\`, or another explicitly disposable location for large intermediate test data. Keep only compact reports and failure evidence in the repository.
- Never delete tracked source, tracked fixtures, reports, logs, manifests, or restart documentation as part of automatic test cleanup. Do not clean another active test process's working directory.
