# Contributing

Thank you for considering a contribution.

Please open an issue before investing in a large change so the intended API, persisted-index compatibility impact, and validation approach can be agreed first.

## Pull requests

- Keep changes focused and include tests or a reproducible validation case.
- Preserve the public API and on-disk catalog contract unless the pull request explicitly proposes a major-version change or an intentional migration path.
- Do not introduce allocations, ownership changes, routing work, I/O amplification, synchronization costs, or durable-index regressions without documenting the reason and measuring the affected path where practical.
- Preserve the declared key and identity contract that determines a catalog's persisted structure.
- Run the library build, focused relevant harness checks, and package-consumer smoke test before submitting.

## Contribution license

By submitting a contribution, you agree that it is your original work and that it is licensed under Apache-2.0.
