# Harness template

The harness: rules, agents, skills, extensions (including on-demand MCP servers) and settings defaults. Ombra ships
`profile/` in its runtime pack (`<pack>/omp/harness/`) and applies it to every session, chat and terminal, through
`OMBRA_HARNESS_DIR`. The user's `~/.omp/agent` items and `config.yml` win over it; nothing is copied into the user's
files.

Documentation: [docs/harness.md](../docs/harness.md). Licenses of adapted skills: [NOTICE.md](NOTICE.md).
