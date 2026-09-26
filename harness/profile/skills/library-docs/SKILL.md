---
name: library-docs
description: Use when you need public documentation or API details of an external library/framework used by the project (usage, config options, breaking changes between versions). Not for understanding this project's own code
---

# Library docs (Context7)

Devices: `xd://mcp__context_resolve_library_id`, `xd://mcp__context_query_docs` (server `context7`; omp drops the digit).

1. Find the version the project actually uses: lockfile (`package-lock.json`, `pnpm-lock.yaml`, `yarn.lock`, `poetry.lock`, `Cargo.lock`, `go.sum`…) first, then the manifest range. No version found → say so.
2. `resolve_library_id` (write JSON `{libraryName, query}`) with `libraryName` and a short generic `query`. Pick the ID whose `Versions` contains the project's version.
3. `query_docs` (write JSON `{libraryId, query}`) with the versioned ID (`/<org>/<repo>/<version tag as listed>`, e.g. `/vitest-dev/vitest/v3_2_4`). If that version is not listed, use the closest listed one and state the mismatch.
4. Answer with the `Source:` links returned (they point to the tagged docs).

## Privacy — what may be sent
- Only: library name, version, and a generic topic ("vitest 3 mock assertions").
- Never: the user's message as is, project code, file paths, internal names, business terms, documents, keys.

## Limits
- Context7 is a third-party service; docs can be incomplete. For behaviour that matters, confirm in the installed package (`node_modules/<pkg>`, types, changelog).
- Service unreachable → report it; fall back to `web_search` with the same generic query, or to the installed package sources.
