# Upstream proposal: opt-in model discovery and remote catalog

Prepared for [can1357/oh-my-pi](https://github.com/can1357/oh-my-pi), against the pinned release
**omp 18.8.0** (Bun 1.4.2). **Not submitted yet.** OMP GUI does not fork or patch omp. The patch
`omp-discovery-opt-in.patch` (next to this file) is only for upstream and is not applied
anywhere in this project. Until upstream ships a fix, OMP GUI relies on an external workaround:
a `--config` overlay that sets `disabledProviders`, plus the optional Strict Network Privacy
allowlist proxy.

---

## Issue

### Title

Model discovery contacts providers that were never configured, and the models.dev mirror cannot be turned off

### Summary

When omp starts, or any time the model cache is stale, it sends unauthenticated `GET …/models`
requests to every built-in provider that allows unauthenticated discovery. It also probes local
model servers on loopback ports and downloads the shared models.dev mirror from
`catalog.stencil.so`. This happens even when the user has never configured those providers.
`disabledProviders` stops the provider probes, but only if the user lists every provider they do
not use, including providers added in later releases. No setting stops the
`catalog.stencil.so` download.

### Reproduction

Clean profile, no credentials, omp 18.8.0:

```sh
# 1. Proxy for outbound traffic on :8899. Bind the three local-server ports too:
#    loopback probes do not go through a proxy, so the reverse listeners record them.
mitmdump --mode regular@8899 \
  --mode reverse:http://127.0.0.1:9@11434 \
  --mode reverse:http://127.0.0.1:9@8080 \
  --mode reverse:http://127.0.0.1:9@1234

# 2. Fresh profile, route omp through the proxy, trust the mitmproxy CA.
REAL_HOME="$HOME"
export HOME="$(mktemp -d)"
export PI_PROXY=http://127.0.0.1:8899 HTTPS_PROXY=http://127.0.0.1:8899
export NODE_EXTRA_CA_CERTS="$REAL_HOME/.mitmproxy/mitmproxy-ca-cert.pem"

# 3. Start omp in RPC mode and ask for the model list.
printf '%s\n' '{"id":"1","type":"get_available_models"}' | omp --mode rpc
```

The same requests also happen without `get_available_models`: they come from the startup
`modelRegistry.refresh("online-if-uncached")` (`packages/coding-agent/src/main.ts`, and the SDK
paths in `sdk.ts`).

### Observed requests

With no credentials:

| Status | Request | Source |
|---|---|---|
| 200 | `GET https://api.commandcode.ai/provider/v1/models` | `commandcode` (unauthenticated discovery) |
| 200 | `GET https://hyper.charm.land/v1/models` | `charm-hyper` |
| 200 | `GET https://api.kilo.ai/api/gateway/models` | `kilo` |
| 200 | `GET https://coding-intl.dashscope.aliyuncs.com/v1/models` | `alibaba-coding-plan` |
| 200 | `GET https://zenmux.ai/api/v1/models` | `zenmux` |
| — | `GET https://api.venice.ai/api/v1/models` | `venice` |
| — | `GET https://catalog.stencil.so/models.json.zstd` | models.dev mirror (`MODELS_DEV_URL`) |
| — | `GET http://127.0.0.1:11434/api/tags` | implicit Ollama probe |
| — | `GET http://127.0.0.1:8080/models`, `GET http://127.0.0.1:8080/props` | implicit llama.cpp probe |
| — | `GET http://127.0.0.1:1234/api/v0/models`, `GET http://127.0.0.1:1234/v1/models` | implicit LM Studio probe |

Every request carried only the `Accept`, `User-Agent: Bun/1.4.2` and `Accept-Encoding` headers,
with no auth and no body.

With a real profile (Anthropic and OpenAI Codex OAuth only), the same unauthenticated requests
happen whenever the model cache is stale: the refresh timestamps in `models.db` matched the
session start times.

Where this happens in the 18.8.0 sources:

- `packages/coding-agent/src/config/model-registry.ts`, `#addImplicitDiscoverableProviders`:
  always registers Ollama, llama.cpp and LM Studio discovery, unless the provider is in
  `disabledProviders` or `models.yml`.
- Same file, `#collectBuiltInModelManagerOptions`: creates a model manager when
  `isAuthenticated(apiKey) || descriptor.allowUnauthenticated || hasExplicitVllmConfig || canUseSharedCatalogWithoutAuth`.
  It also runs a "catalog-only providers" loop that attaches `modelsDevCatalogFallback` without
  any credentials.
- `packages/catalog/src/provider-models/openai-compat.ts`: `fetchCatalogPayload` downloads
  `https://catalog.stencil.so/models.json.zstd`. The additive models.dev layer and the
  reference/pricing enrichment inside several endpoint fetchers (Anthropic, SiliconFlow,
  Fireworks, effort ladders) all download it.

### Expected

omp only connects to endpoints the user chose: providers with credentials or explicit
configuration, and local servers the user pointed it at. Users should be able to opt out of the
shared remote catalog. The current behavior can stay the default.

### Proposal

Add two settings in the existing `providers.*` namespace, registered like `disabledProviders` in
`config/model-settings.ts` and shown in the settings panel under **Providers → Privacy**:

| Setting | Values | Default | Effect |
|---|---|---|---|
| `providers.discovery` | `all` \| `configured` | `all` | `all`: current behavior. `configured`: model lists are fetched only from providers the user set up. |
| `providers.remoteCatalog` | boolean | `true` | `false`: omp never fetches the models.dev mirror. Model metadata comes from the bundled catalog plus the endpoints of configured providers. |

```yaml
# config.yml (or a --config overlay)
providers:
  discovery: configured
  remoteCatalog: false
```

`providers.discovery: configured` semantics:

- A provider counts as *configured* when it has any credential that `authStorage.keys.source()`
  sees, or a keyless login (`keys.keyless`), or a `models.yml` entry, or a runtime/extension
  override. Credentials here means stored OAuth, a `/login` API key, the provider's env var, a
  `--api-key`, or a `models.yml` key. Only configured providers get a discovery manager or the
  catalog-only models.dev layer.
- Ollama, llama.cpp and LM Studio are probed only when their base-URL env var is set
  (`OLLAMA_BASE_URL`/`OLLAMA_HOST`, `LLAMA_CPP_BASE_URL`, `LM_STUDIO_BASE_URL`) or when they are
  configured as described above. `models.yml` discovery entries work as before.
- Unchanged: Apple Foundation Models discovery (on-device, no socket I/O), the keyless `local` and
  `web` providers, and model managers registered by extensions (the user installed them).
- **Logging in or adding a key enables discovery for that provider.** Settings and credentials
  are read on every registry refresh, so the provider is discovered on the next refresh, and at
  the latest on the next startup. No allowlist needs to be edited.
- `disabledProviders` still takes precedence: a disabled provider is never contacted, even when
  it is configured.

`providers.remoteCatalog: false` semantics:

- `@oh-my-pi/pi-catalog` gets a process-wide switch, `setRemoteCatalogEnabled()`, checked in
  `fetchRevalidatedWellKnownModels`. All catalog lookups go through that function, so while the
  switch is off every lookup rejects without I/O. The existing callers already treat a failed
  catalog fetch as "use bundled metadata". The coding agent drives the switch with
  `effect(cfgProvidersRemoteCatalog, setRemoteCatalogEnabled)`, which follows the
  `secrets.enabled` → `configureCredentialRedaction` pattern.
- The registry also stops attaching the `modelsDev` layer to managers, and skips the
  catalog-only loop. Otherwise a layer that fails on every refresh would keep cached snapshots
  non-authoritative and retry every 5 minutes.
- Trade-off: models released after the installed omp build show up only through the
  configured providers' own `/models` endpoints, or after `omp update`.

### Privacy rationale

- Each unauthenticated probe tells a third party that this IP address runs omp, and when. A
  `User-Agent: Bun/1.4.2` request to `hyper.charm.land`, `api.kilo.ai`, `zenmux.ai`,
  `api.venice.ai`, `api.commandcode.ai` and DashScope at every session start is a stable
  fingerprint. The user has no relationship with these vendors.
- `catalog.stencil.so` is a third-party mirror. It gets the same signal on every stale-cache
  start, including for users who only use first-party providers.
- Loopback probes to well-known ports reach whatever happens to listen there (dev servers,
  other tools). In managed environments they show up as unexpected traffic.
- The only opt-out today, `disabledProviders`, is a deny-list. It has to name every provider
  omp ships and grows with every release, which makes "no traffic to providers I did not add"
  impossible to maintain. It also cannot cover the remote catalog.
- Environments with egress restrictions (corporate proxies, audits, air-gapped labs) need a
  documented switch, not a firewall rule that breaks discovery silently.

---

## Pull request

### Title

feat(coding-agent, catalog): opt-in model discovery (`providers.discovery`) and `providers.remoteCatalog`

### Description

Fixes #<issue>. This PR adds two settings so that model discovery and the shared models.dev
mirror can be limited to what the user configured. The defaults keep today's behavior.

**`packages/catalog`**

- New `src/provider-models/remote-catalog.ts`, which exports `setRemoteCatalogEnabled()` and
  `isRemoteCatalogEnabled()`. It is also exported from `provider-models/index.ts`.
- `openai-compat.ts`: `fetchRevalidatedWellKnownModels` rejects without a request while the
  switch is off. This is the single choke point for the models.dev fallback layer and for the
  reference/pricing enrichment inside endpoint fetchers, all of which already catch the
  failure.

**`packages/coding-agent`**

- `config/model-settings.ts`: registers `providers.discovery` (enum `all` | `configured`,
  default `all`) and `providers.remoteCatalog` (boolean, default `true`) under
  Providers → Privacy. It also binds `effect(cfgProvidersRemoteCatalog, setRemoteCatalogEnabled)`.
- `config/model-registry.ts`:
  - New `#hasProviderConfiguration(providerId)`: true for any credential source, a keyless
    login, a `models.yml` entry, a runtime override, or a keyless provider.
  - `#addImplicitDiscoverableProviders`: in `configured` mode, Ollama, llama.cpp and LM Studio
    are added only with an endpoint env override or a configuration.
  - `#collectBuiltInModelManagerOptions`: in `configured` mode, unauthenticated discovery
    requires `#hasProviderConfiguration`, and the catalog-only loop skips unconfigured
    providers. With `remoteCatalog: false`, managers lose their `modelsDev` layer and the
    catalog-only loop does not run.
  - `#loadCachedStandardProviderModels`: `discoveryExpected` mirrors the same gates, so
    providers that will not be discovered are not left in the `idle` state (which
    `isProviderDiscoveryPending` reports).
- Both CHANGELOGs get `[Unreleased]` → `### Added` entries.

Not changed: default behavior, `disabledProviders` precedence, `models.yml` discovery, extension
model managers, Apple on-device discovery, and inference/OAuth traffic.

**Testing.** The published packages ship no tests near `config/` or `provider-models/`, so this
PR adds none. Suggested coverage in the monorepo's model-registry tests, using an injected
`fetch` that records URLs:

1. `providers.discovery: configured` with no credentials: no request to any `allowUnauthenticated`
   provider, to loopback ports, or to `catalog.stencil.so`.
2. Same setting with a stored key for one provider: only that provider's endpoint, plus the
   catalog unless it is disabled.
3. `providers.remoteCatalog: false` with the default discovery mode: no request to
   `catalog.stencil.so`, and Anthropic discovery still succeeds when authenticated, with no
   models.dev references.
4. A provider in `disabledProviders` is never contacted, even when configured.

The patch is `avalonia/docs/upstream/omp-discovery-opt-in.patch` in the OMP GUI repository. It
is a git-style unified diff with `a/packages/…` / `b/packages/…` paths, matching the monorepo
layout taken from each package's `repository.directory`. It was checked with
`patch --dry-run -p1` and `git apply --check` against the 18.8.0 published sources laid out as
`packages/catalog` and `packages/coding-agent`. All changed files transpile with Bun 1.4.2.
A full `tsc`/test run has not been done, because the published packages do not include the
monorepo toolchain.
