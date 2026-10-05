# Changelog

All notable changes to this project are documented in this file.

## 1.2.0 - 2026-10-05

- Launch API correction: added verb-first typed Create/Open/CreateOrOpen methods at catalog and index-set level.
- Breaking syntax change: indexers retrieve existing index sets/index handles only and throw for missing names. Builder configuration is explicit through IndexSet/Define.
- Migrated examples and dependent Abraxas callers; a catalog can contain multiple independent identity universes.
- Documented absolute/relative directory resolution and UNC/network storage performance caveats. Examples use absolute paths.
- No catalog file-format change. The superseded 1.0.0 and 1.1.0 releases are retired after replacement verification.

## 1.1.0 - 2026-10-05

- Added direct catalog name/directory overloads for Create, Open, CreateOrOpen, and Compact, plus GetFilePath for sidecar resolution.
- Removed CatalogLocation construction and storage from ordinary catalog lifetime paths; retained the published compatibility APIs.
- Added Name and DirectoryPath metadata directly on Catalog and simplified the quick start.
- Expanded package-consumer checks for named lifecycle, invalid names, missing/existing files, compaction, and legacy API interoperability.

## 1.0.0 - 2026-10-05

- Initial public release.
- Added durable and memory-only typed key-to-identity catalogs for application-owned data.
- Added scalar, fixed-width, variable, and composite index structures matched to the declared key and identity contract.
- Added ordered readers, streaming identity/key retrieval, conditions, range/count workloads, maintenance, backup, compaction, repack, recoverable-format, and concurrency-admission workflows.
- Added Apache-2.0 licensing, SourceLink, symbol packages, deterministic builds, package validation, package-consumer smoke validation, CI, and trusted release automation.
