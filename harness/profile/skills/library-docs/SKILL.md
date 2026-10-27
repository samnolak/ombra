---
name: library-docs
description: "External library/framework docs: API usage, config options, breaking changes; not this project."
---

# Library docs (Context7)

Context7 is an on-demand server of the `mcp_lazy` tool (server `context7`); it is off until you enable it.

- Enable: `mcp_lazy` with `{"action": "enable", "server": "context7"}`. The reply lists the server's tools and input schemas; use the exact tool names it shows (`resolve-library-id`, `query-docs` or `get-library-docs`).
- Call: `{"action": "call", "server": "context7", "tool": "<name>", "args": {…}}`.
- Disable: `{"action": "disable", "server": "context7"}` once you have the docs.

1. Find the version the project actually uses: lockfile (`package-lock.json`, `pnpm-lock.yaml`, `yarn.lock`, `poetry.lock`, `Cargo.lock`, `go.sum`…) first, then the manifest range. No version found → say so.
2. Enable `context7`, then call `resolve-library-id` (args `{libraryName, query}`) with `libraryName` and a short generic `query`. Pick the ID whose `Versions` contains the project's version.
3. Call the docs tool (args `{libraryId, query}`) with the versioned ID (`/<org>/<repo>/<version tag as listed>`, e.g. `/vitest-dev/vitest/v3_2_4`). If that version is not listed, use the closest listed one and state the mismatch.
4. Disable `context7`. Answer with the `Source:` links returned (they point to the tagged docs).

## Privacy — what may be sent
- Only: library name, version, and a generic topic ("vitest 3 mock assertions").
- Never: the user's message as is, project code, file paths, internal names, business terms, documents, keys.

## Limits
- Context7 is a third-party service; docs can be incomplete. For behaviour that matters, confirm in the installed package (`node_modules/<pkg>`, types, changelog).
- `enable` fails or the service is unreachable → report it; fall back to `web_search` with the same generic query, or to the installed package sources.
