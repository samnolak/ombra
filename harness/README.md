# Harness profile template

The omp profile `harness` (`~/.omp/profiles/harness`): rules, agents, skills, base settings and MCP servers. Files
ending in `.seed` create the user-owned files only if they are missing. The desktop client uses an existing `harness`
profile in place; its CI instantiates this template for the real-omp tests.

Documentation and setup: [docs/harness.md](../docs/harness.md). Licenses of adapted skills: [NOTICE.md](NOTICE.md).
