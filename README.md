# Application Intelligence Platform

Implementation of **Architecture v1.0** (frozen). Point it at an application's repositories → it
analyzes the code, builds a **Knowledge Model**, and projects that model into a browsable, versioned
documentation site. The Knowledge Model is the product — every service here exists to build it,
maintain it, query it, or consume it.

> Architecture of record: Core Domain · Knowledge Model · Analysis Platform · Platform Architecture ·
> Product & Technical Specification. This code implements those documents; it does not redefine them.

This platform is the successor to [`DocPlatform`](../DocPlatform) — it takes that project's proven
extraction/AI approach and its roadmap (incremental docs, a persisted knowledge graph, impact
analysis, a plugin/registry control plane, an event-driven trigger) and makes them the *architecture*
rather than the backlog.

---

## What it does

```
Application → Repository Discovery → Language Engines + Plugins (analyzers emit Discoveries)
   → Validation (the ONLY creator of Knowledge) → Snapshot (immutable, append-only, persisted)
   → Relationship Resolution → Validation → Snapshot → Projection → Document Store (versioned)
```

Two apps, one store: **Aip.Host** (the Creator) writes a new documentation **version** into the
document store on every run that finds real changes; **Aip.Viewer** (the reader) reads it back live,
on every request — nothing is cached or materialized to disk. Every past version stays reachable, and
the Viewer offers a version picker to switch between them.

Documentation belongs to an **application**, not a repository — an app can span many repos, and the
Knowledge Model describes the whole thing. Every fact carries **Evidence** (a domain invariant, enforced
at construction), so nothing in the model is ungrounded.

### Current status

The platform is **functional end to end**, not a skeleton. It builds clean with zero warnings, the
test suite passes (31 tests), and the bundled `demo` runs the full workflow against a sample application:

- discovers knowledge nodes and relationships from the sample backend + frontend,
- resolves frontend→backend call mappings (`GET /api/customer → GET /api/Customer`, conf 0.90),
- answers impact and search queries over the model,
- generates Markdown documents and publishes them as a new version in the document store (Aip.Viewer
  reads them live),
- performs a correct incremental re-run (analyzes only touched artifacts, carries the rest forward —
  and this carry-forward survives across processes, since the Knowledge Store is persisted to SQL Server).

Two invocation modes are wired (see **Run** below): standalone/scheduled (`serve`, the intended
production shape) and batch (`apps.yml`, auto-diffed against each repo's last analyzed commit) —
`serve` calls the exact same batch logic internally. There is no separate CI/CD/webhook trigger.

---

## Key design principles

1. **Code analyzes; the AI only explains.** Deterministic analyzers build the Knowledge Model; the AI
   Platform receives that grounded model — **never the raw repository** — so it can't invent facts.
   With no AI token configured, projection is fully deterministic.
2. **Validation is the sole write gate.** Nothing becomes Knowledge except through the Validation
   Pipeline — including the Relationship Resolution Engine's output, which passes back through the
   *same* gate. It also checks each node's kind against the Schema Registry (§ below) — an
   unrecognized kind is surfaced as a warning, never silently dropped. A relationship whose endpoint
   lies outside the analyzed estate (a third-party library type, an external API) is preserved too —
   Validation synthesizes a minimal `External` node (a fixed Core kind, always registered) for the
   unresolved endpoint instead of dropping the relationship; only a relationship where *neither*
   endpoint is known is rejected.
3. **Every fact carries Evidence.** `KnowledgeNode.Create` / `Relationship.Create` reject facts without
   evidence; grounding is a structural invariant, not a convention. When evidence disagrees on a node's
   kind, the losing kind isn't discarded — it's kept as an `AlternateKinds` property alongside the
   winning one, so the ambiguity survives into the graph itself, not just a log line.
4. **Snapshots are immutable and append-only.** Each run commits a new snapshot to the persisted
   Knowledge Store; incremental runs diff the model and carry forward untouched knowledge. Published
   documentation follows the same shape — each publish is a new, additive **version**, never an
   overwrite of the last one.
5. **Extend by adding, never editing the Core.** New technology = a **plugin**, new output = a
   **projection**, new vocabulary = the **Schema Registry** (the union of every loaded plugin's own
   declared capabilities — no hand-maintained central list). Any change to a frozen decision is
   recorded as a **future ADR**, not applied.

---

## Architecture (Clean Architecture, 19 projects)

```
Aip.slnx
├── Directory.Build.props            net10.0, nullable, implicit usings (+ ApplicationId alias)
└── src/
    ├── Aip.Core                     Domain (Knowledge Model) + Core PORTs  → depends on nothing
    ├── Aip.Abstractions             Platform service contracts             → Core
    │
    ├── Aip.Analysis                 Analysis pipeline (write path)
    ├── Aip.Knowledge                Validation gate + Relationship Resolution Engine
    ├── Aip.Projections              Projection engine (read path)
    ├── Aip.Query                    Query platform (impact / search / history)
    ├── Aip.Ai                       AI platform + token accounting (dual-use)
    ├── Aip.Registries               Application / Schema / Plugin registries (control plane)
    ├── Aip.Observability            Metrics / reporting
    │
    ├── Aip.Infrastructure           Adapters behind the ports (persisted knowledge store, sourcing,
    │                                 AI provider, Run History, filesystem document store)
    ├── Aip.Infrastructure.AzureBlob Azure Blob Storage document store adapter (standalone/production swap-in)
    ├── Aip.Engines.Roslyn           C# language engine (real MSBuildWorkspace-backed resolution)
    ├── Aip.Engines.TypeScript       TypeScript language engine (Angular/React/Next.js) + shared
    │                                 frontend-auth detection
    ├── Aip.Plugins.AspNetCore       ASP.NET Core technology plugin
    ├── Aip.Plugins.Angular          Angular technology plugin
    ├── Aip.Plugins.React            React technology plugin
    ├── Aip.Plugins.NextJs           Next.js technology plugin (reuses the React analyzers)
    │
    ├── Aip.Host                     Document Creator — composition root + CLI trigger + entry point
    └── Aip.Viewer                   Document Viewer — reads the store live, renders on request

tests/Aip.Tests                      Domain, validation, resolution/projection, document store, end-to-end
samples/                             Bundled sample app (ASP.NET Core backend + Angular frontend)
```

### Dependency rule

`Host → (all) → Aip.Abstractions → Aip.Core`. The Core depends on nothing; nothing depends on
Infrastructure. Concrete implementations are wired in exactly one place: `PlatformComposition`.

---

## Prerequisites

- **.NET SDK 10** (`dotnet --version` ≥ `10.0`) — the Viewer is a .NET app too, no Node/npm needed.
- **A reachable SQL Server** — Run History and Logging are SQL Server / Azure SQL only, with no
  zero-setup fallback (no SQLite). For local dev, a Docker SQL Server container is the fastest path —
  see **Local SQL Server (Docker)** below. Production points the same connection strings at Azure SQL.
- **Network access to NuGet** when analyzing a C#/.NET application. The C# engine runs a real
  `dotnet restore` against each analyzed repository (via MSBuildWorkspace) so its actual package
  assemblies — not just the .NET BCL — are loaded into the compilation; this is what lets analyzers
  resolve real third-party SDK types and methods instead of guessing from source text. The first analysis
  of a given repo/package set needs network access to NuGet; later runs reuse the shared local NuGet
  cache and are fast. If restore or the MSBuild project load fails for any reason (offline, a private
  feed needing auth, an unusual solution layout), that repository falls back automatically to BCL-only
  analysis — syntax-level facts are unaffected, only resolution of external SDK usage is lost for that run.
- *(Optional)* an AI provider to enhance projection prose. Without one, output is deterministic. Two
  OpenAI-compatible providers are supported — both sets of credentials can sit in config at once, and
  `Ai:Provider` (or `AIP_AI_PROVIDER`) picks which is active, so switching is a one-line config change,
  never a clear-and-repaste of a key:
  - **`Auto`** (default) — Azure Foundry if `Ai:AzureFoundry:ApiKey` is set, else GitHub Models if
    `Ai:GitHubToken`/`AIP_GITHUB_TOKEN` is set, else deterministic (no AI).
  - **`AzureFoundry`** — force Azure Foundry. Set `Ai:AzureFoundry:Endpoint` (the deployment's
    `.../openai/v1` base URL), `Ai:AzureFoundry:ApiKey`, and `Ai:AzureFoundry:Deployment` (the model
    deployment name).
  - **`GitHubModels`** — force GitHub Models even if a Foundry key is also configured. Set
    `AIP_GITHUB_TOKEN` (a fine-grained PAT with **Models: Read-only**); optionally `AIP_AI_MODEL`
    (default `openai/gpt-4o-mini`) and `AIP_AI_ENDPOINT`.

  Whichever provider is active, it's wrapped in several layers of resilience — retry with backoff,
  fallback model, fallback provider, and an optional TPM throttle — before a page ever falls back to
  deterministic rendering. See **AI reliability** below.

---

## Build & test

```bash
dotnet build Aip.slnx
dotnet test  Aip.slnx
```

## Run

The platform analyzes an estate three ways. All three normalize to the same `ExecutionRequest` and flow
through the **one** pipeline — *"the pipeline is identical across modes; only the trigger differs."*

### Standalone / scheduled (`serve`) — the intended production shape

No CI/CD pipeline involved: the platform runs as a normal, addressable app exposing one endpoint,
`POST /run`, and something external calls it once a day (a Timer-triggered Azure Function doing nothing
but that one HTTP call is the intended shape — see **Deployment** below). Each call kicks off the exact
same batch logic as `run --config apps.yml` below (pulls every app's repos, auto-diffs, updates the
Knowledge Model, republishes only when something actually changed) on a background task, returning
`202 Accepted` immediately rather than holding the HTTP connection open for a run that can take minutes:

```bash
dotnet run --project src/Aip.Host -- serve --config apps.yml
# → POST http://localhost:5000/run  (add header X-Run-Key: <value> if Run:Secret/AIP_RUN_SECRET is set)
```

A second `/run` call while one is already in flight gets `409 Conflict`, not a second overlapping run —
the version-index read-modify-write in `PublishVersionAsync` is only safe with one writer at a time, and a
real daily cadence never legitimately needs two runs at once. `GET /health` is a bare liveness check for
whatever's hosting the container.

### Batch mode (`apps.yml`) — for local testing, or as what `serve` calls internally

Declare the estate in `apps.yml` (an application can span many repos; each repo is a local path or a
git URL that gets shallow-cloned):

```yaml
applications:
  - name: ShopApp
    repos:
      - samples/backend      # local path (relative to apps.yml) or a git URL
      - samples/frontend
  - name: MyService
    skipIfUnchanged: true    # skip this app's whole run if every repo is at its last-analyzed commit
    repos:
      - https://github.com/acme/my-service.git#develop   # add #branch to target a non-default branch
```

`skipIfUnchanged` (default `false`) checks each repository's current commit against the last one recorded
in Run History (`RepositoryRuns.CommitSha`, keyed by application + repo location) before doing anything
else. If every repo in the app is unchanged since its last analyzed commit, the whole run is skipped —
no clone-and-scan, no AI cost, no new documentation version — and it's recorded in `Runs` with
`Status = "Skipped"`. Any repo that's new, moved, or failed to materialize forces a normal run.

When a repo *has* changed, batch mode no longer reanalyzes it blindly: after materializing at the new
commit, `GitRepositorySource` diffs it against the commit last recorded in Run History (a lightweight
`git fetch --depth 1 origin <previous>` followed by `git diff --name-only`, since the standard
materialization is a shallow clone with no local history beyond HEAD) and only the artifacts under
changed files are re-analyzed — the rest of that repo's knowledge carries forward from the previous
Knowledge snapshot. A repo whose diff can't be determined (no previous commit on record — the first time
it's analyzed — or the diff itself fails, e.g. the previous commit is no longer reachable after a
force-push) is conservatively treated as fully changed rather than silently skipped. Once the Knowledge
Model is updated, publishing is *also* skipped if the resulting diff is completely empty (e.g. a
comment-only edit) — a changed commit doesn't automatically mean a new documentation version.

A repo entry is a local path, a git URL (shallow-cloned from its default branch), or `url#branch` to
clone a specific branch. Git submodules are initialized automatically after a shallow clone if the repo
has a `.gitmodules` file (best-effort — a submodule init failure logs a warning and analysis proceeds
without that submodule's content, rather than failing the whole repo). **Private repos** need no special
marker in `apps.yml` — configure a PAT for the repo's *host* under `Git:Credentials` (in
`appsettings.json`/`appsettings.Development.json`, or an env var of the same shape, e.g.
`Git__Credentials__dev.azure.com`), and any repo on that host clones authenticated automatically; public
repos on the same host are unaffected. One token per host covers every repo there — no per-repo secrets.

```json
// appsettings.Development.json (gitignored)
{ "Git": { "Credentials": { "dev.azure.com": "<Azure DevOps PAT>" } } }
```

```bash
# Auto-diffed analysis of every application in the file; publishes a new documentation version only for
# the apps that actually had a real Knowledge Model change.
dotnet run --project src/Aip.Host -- run --config apps.yml
```

Incremental carry-forward reads the prior snapshot from the persisted Knowledge Store (SQL Server, see
**Knowledge Store** below), so it works across process restarts and across machines, not just within one
run — an app's very first analysis is always a full rebuild, and every run after that is incremental by
default.

### Diagnostics

```bash
dotnet run --project src/Aip.Host            # compose + validate the service graph, then exit
dotnet run --project src/Aip.Host -- verify  # exercise Core Domain invariants, then exit
dotnet run --project src/Aip.Host -- demo    # scripted end-to-end walkthrough against samples/
```

## Local SQL Server (Docker)

Run History and Logging are SQL Server / Azure SQL only — there is no SQLite fallback, so a missing
connection string is a startup failure, not a silent degrade. For local dev, the fastest path is a
throwaway SQL Server container:

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Aip_Local_Dev_2026!" \
  -p 1433:1433 --name aip-sql-local \
  -v aip-sql-data:/var/opt/mssql \
  -d mcr.microsoft.com/mssql/server:2022-latest
```

The named volume (`aip-sql-data`) persists data across container restarts — `docker stop`/`docker start
aip-sql-local` keeps everything; only `docker rm` (with the volume) actually discards it. Two databases
are needed — Run History and Logging are deliberately separate (see **Logging** below):

```bash
docker exec aip-sql-local /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Aip_Local_Dev_2026!" -C \
  -Q "CREATE DATABASE AipHistory; CREATE DATABASE AipLogs;"
```

Point `appsettings.Development.json` at the container (already the checked-in local default — see
**Configuration** below):

```json
{
  "History": { "ConnectionString": "Server=localhost,1433;Database=AipHistory;User Id=sa;Password=Aip_Local_Dev_2026!;TrustServerCertificate=True;" },
  "Logging": { "ConnectionString": "Server=localhost,1433;Database=AipLogs;User Id=sa;Password=Aip_Local_Dev_2026!;TrustServerCertificate=True;" }
}
```

This local setup is deliberately a stepping stone, not the end state: schema and data get validated
against a local SQL Server first, then the connection strings switch to Azure SQL once confirmed —
no code change either way, purely a config swap (same pattern as every other store in this platform).

## Run History

Every pipeline execution is recorded to a durable **Run History** database — the same
SQL Server database the Knowledge Store (see below) and `AiFallbackEvents`/`DocumentVersionChanges` also
live in, separate only from the Logs database (see **Logging**). Five tables:

- **`Runs`** — one row per execution: application, trigger type, status, AI provider/tokens spent, and
  pages/knowledge nodes/relationships produced.
- **`RepositoryRuns`** — one row per repository materialized during a run: application and repository
  name, location, branch, the exact commit analyzed (the basis for incremental reuse and
  `skipIfUnchanged`), and `SourceKind` — `Local` (a filesystem path), `PublicGit` (cloned with no
  credentials), or `PrivateGit` (cloned using a `Git:Credentials` PAT for that host) — so you can see at
  a glance whether a repo needed authorization.
- **`AiFallbackEvents`** — one row per page that fell back to deterministic rendering, with why. See
  **AI reliability** below.
- **`Snapshots`** — one row per committed Knowledge Model snapshot (append-only; see **Knowledge Store**
  below).
- **`DocumentVersionChanges`** — one row per published documentation version that had a predecessor to
  diff against, backing the Viewer's "What changed" page. See **Document Viewer** below.

Set `History:ConnectionString` (appsettings) or `AIP_SQL_CONNECTION_STRING` (env var, always wins) to a
SQL Server / Azure SQL connection string — see **Local SQL Server (Docker)** above for the local default.
`Aip.Host` applies pending migrations on every startup by default — set `History:AutoMigrate` to `false`
(or `AIP_HISTORY_AUTO_MIGRATE=false`) to turn that off and manage the schema yourself instead (e.g.
`dotnet ef database update` as an explicit CI/CD step).

If you change any entity in `src/Aip.Infrastructure/RunHistoryStore.cs`
(`RunEntity`/`RepositoryRunEntity`/`AiFallbackEventEntity`/`SnapshotEntity`/`DocumentVersionChangeEntity`),
generate a new migration with the [EF Core CLI tool](https://learn.microsoft.com/ef/core/cli/dotnet):

```bash
dotnet tool install --global dotnet-ef   # if you don't already have it
dotnet ef migrations add <Name> --project src/Aip.Infrastructure
```

This targets the local Docker SQL Server by default (`RunHistoryDbContextFactory`) — override with
`AIP_SQL_CONNECTION_STRING` to generate a migration against a different target. Migrations are
provider-specific in EF Core, but this store is SQL Server end to end (local Docker and Azure SQL are the
same provider), so the same migration applies either way; only the connection string changes.

## Logging

Every log the platform emits — from `LogLevel.Debug` (per-artifact analysis detail) up through
`LogLevel.Error` (exceptions, restore/workspace failures) — goes to two places: the console, for a human
watching a run happen, and a durable, queryable `Logs` table (Serilog, auto-created on first write), so
nothing about a run goes unrecorded once the process exits. `Microsoft.*`/`System.*` framework noise
(EF Core's own SQL command logging, etc.) is raised to `Information` so it doesn't drown out the app's own
signal, but is still fully captured, not suppressed.

The Logs database is **deliberately separate from Run History's** — different connection string, so the
two can be scaled, retained, and moved to Azure SQL independently of each other. Set
`Logging:ConnectionString` (appsettings) or `AIP_LOGGING_SQL_CONNECTION_STRING` (env var, always wins) —
see **Local SQL Server (Docker)** above for the local default. Both `Aip.Host` and `Aip.Viewer` wire this
up the same way (`Aip.Infrastructure/LoggingModule.AddAipLogging`); `Aip.Viewer` additionally logs one
structured entry per HTTP request (method, path, status code, duration).

Unlike Run History, the `Logs` table's schema isn't EF-migrated — Serilog's SQL sink creates it
automatically from a fixed, standard shape (timestamp, level, message, exception, and the full structured
properties as JSON) the first time anything is logged.

## AI reliability

A page only ends up deterministic after every layer below has genuinely failed — "deterministic" means
"AI was tried and failed," never "AI wasn't tried" or "AI hit an avoidable rate limit."

- **Retry** — `OpenAiCompatibleProvider` retries transient failures (429 rate-limited, and 5xx) with
  exponential backoff, honoring the provider's `Retry-After` header when present (capped at 3s so a long
  wait never stalls a whole run). Configurable: `Ai:MaxRetries` (default 3), `Ai:TimeoutSeconds` (default
  100), `Ai:RetryDelayMs` (default 500, the backoff base).
- **Model switching** — set `Ai:AzureFoundry:FallbackDeployment` (or `Ai:FallbackModel` for GitHub
  Models) to a second deployment on the *same* provider. If the primary model exhausts its retries, the
  same request is retried against the fallback model before failing over further — useful when one
  specific deployment is degraded, overloaded, or deprecated independently of the provider itself.
- **Provider failover** — when *both* `Ai:AzureFoundry:ApiKey` and `Ai:GitHubToken` are configured, a
  page only goes deterministic once *both* providers (each having already tried its own model-fallback
  tier above) have failed. `Ai:Provider` still controls which is primary; the other becomes the failover
  target automatically — no extra config needed beyond having both credentials present.
- **TPM (tokens-per-minute) throttling** — optional, off by default (`0` = disabled; this app can't know
  a deployment's real quota unless told). `Ai:MaxTokensPerMinute` sets a shared default; override per
  deployment with `Ai:AzureFoundry:MaxTokensPerMinute` / `Ai:AzureFoundry:FallbackMaxTokensPerMinute` /
  `Ai:FallbackMaxTokensPerMinute` — a primary and its fallback deployment are frequently sized very
  differently (e.g. a small, deliberately cheap fallback), so each tier's throttle is independent, never
  shared. When enabled, calls are paced to stay under budget using a sliding 60-second window *before*
  hitting a real 429 — admission uses a cheap estimate, corrected by the real reported usage after every
  call.

Every time a page still ends up deterministic after all of the above, one row is recorded in
**`AiFallbackEvents`** (same database as Run History) — application, repositories, section/page, a typed
reason (`RateLimited` / `Timeout` / `NetworkError` / `MalformedResponse` / `ProviderUnavailable` /
`ProviderError(nnn)`), and the underlying error detail. It's also mirrored as a `Warning`-level `Logs`
entry, so "why did this page skip AI" is answerable from either place:

```sql
SELECT Application, Section, Reason, Repositories, OccurredAt FROM AiFallbackEvents ORDER BY OccurredAt DESC;
-- or, from the Logs table:
SELECT * FROM Logs WHERE Message LIKE 'AI fallback:%' ORDER BY TimeStamp DESC;
```

A provider failing over to its secondary (not yet a full fallback to deterministic) is logged separately,
also at `Warning`: `AI provider {Primary} failed — failing over to {Secondary}`.

The version changelog (`IVersionChangelogGenerator`, see **Document Viewer**) is just another AI-assisted
artifact built on this exact same `IAiPlatform`/retry/failover/TPM-throttle/fallback-tracking path —
nothing about "what changed" needed its own resilience layer.

## Document Viewer

Every application published gets a **Product Specification** (overview, features, use-cases) and a
**Technical Specification** (architecture, API reference, technology stack, data & storage, frontend,
security & authentication). `Aip.Viewer` is a separate, standalone app that renders them — reading the
document store **live, on every request**. Nothing is cached, nothing is written to disk; delete
`output/` entirely and the Viewer still serves everything straight from the store.

```bash
dotnet run --project src/Aip.Viewer   # → http://localhost:5000
```

The landing page (`/`) lists every documented application (from the shared applications index the
Creator maintains) and links to its **latest** version. Each app's pages live at
`/<app-slug>/v<N>/<path>` — the URL always shows exactly which version you're looking at, and a version
picker in the right-hand rail (a plain `v1`/`v2`/… dropdown, populated from that application's version
history) lets you switch between any past version and the current one; below it, a details block shows
that version's creation date and, per repository, its name and short commit SHA. `/<app-slug>/<path>`
with no version number resolves "latest" and redirects (302, since latest changes over time) to the
pinned URL. Points at the same `Storage:ConnectionString` / `AIP_BLOB_CONNECTION_STRING` config as the
Creator, so both apps always agree on where docs live.

Every page also shows a small provenance badge next to the version picker — 🧠 if that specific page's
prose was AI-written this version, ⚙️ if it was rendered deterministically (either by design, or because
every AI tier in **AI reliability** above failed for that page); hovering it shows which, in plain
language. This is per-page, read from that version's manifest (`DocumentManifestEntry.AiWritten`) — not a
blanket "AI was used somewhere in this run" indicator. A URL for a page/version that no longer exists (a
stale bookmark, an old version number) renders a themed 404 page rather than a raw error string.

Below the version details, a **"What changed →"** link appears for any version that has a real predecessor
to compare against (never v1, and never a version whose publish hit the empty-diff skip in **Batch mode**
above). It opens `/<app-slug>/v<N>/changes` — an AI-authored changelog (falling back to a deterministic
structured summary if AI is unavailable or fails, same resilience path as every other AI-written page) plus
a hard-facts stat strip (nodes/relationships added/removed) and each repository's commit delta for that
version. The Viewer reads this from the same SQL Server database as Run History (`DocumentVersionChanges`
— see **Run History** above), via `History:ConnectionString`/`AIP_SQL_CONNECTION_STRING`, same as the
Creator.

## Configuration

Settings are layered, lowest to highest precedence:

1. **`appsettings.json`** (repo root, committed) — structure and safe defaults only, no real secrets.
2. **`appsettings.Development.json`** (repo root, gitignored) — real local values for dev/testing.
3. **Environment variables** — always win. This is exactly how production supplies secrets: a pipeline
   or App Service sets an env var (sourced from a Key Vault-linked secret), and this same code path
   picks it up automatically — no separate "production mode," no code branching.

```json
// appsettings.json (committed template)
{
  "Ai": {
    "GitHubToken": "", "Model": "openai/gpt-4o-mini", "Endpoint": "https://models.github.ai/inference",
    "FallbackModel": "", "MaxRetries": 3, "TimeoutSeconds": 100, "RetryDelayMs": 500, "MaxTokensPerMinute": 0,
    "AzureFoundry": { "Endpoint": "", "ApiKey": "", "Deployment": "", "FallbackDeployment": "" }
  },
  "Storage": { "ConnectionString": "", "Container": "documents" }
}
```

Copy it to `appsettings.Development.json` and fill in real values for local use, or set the equivalent
environment variables (`AIP_GITHUB_TOKEN`, `AIP_AI_MODEL`, `AIP_AI_ENDPOINT`, `AIP_AZURE_FOUNDRY_ENDPOINT`,
`AIP_AZURE_FOUNDRY_API_KEY`, `AIP_AZURE_FOUNDRY_DEPLOYMENT`, `AIP_BLOB_CONNECTION_STRING`,
`AIP_BLOB_CONTAINER`, `AIP_SQL_CONNECTION_STRING`, `AIP_LOGGING_SQL_CONNECTION_STRING`) — either path
works identically. AI reliability tuning (all optional — see **AI reliability** above for what each
does): `AIP_AI_MAX_RETRIES`, `AIP_AI_TIMEOUT_SECONDS`, `AIP_AI_RETRY_DELAY_MS`, `AIP_AI_MAX_TPM`,
`AIP_AI_FALLBACK_MODEL`, `AIP_AI_FALLBACK_MAX_TPM`, `AIP_AZURE_FOUNDRY_FALLBACK_DEPLOYMENT`,
`AIP_AZURE_FOUNDRY_MAX_TPM`, `AIP_AZURE_FOUNDRY_FALLBACK_MAX_TPM`.
`AIP_OUTPUT` controls where local output lives (base directory for the filesystem document store; default
the current directory) — optional, sensible default. `Run:Secret` / `AIP_RUN_SECRET` (optional; unset means
no check) is the shared-secret header `serve` mode's `POST /run` requires — see **Deployment** below.

### AI narrative

Pages are descriptive by default. To have the AI Platform rewrite every page into fuller prose (grounded
in the same model, never the source — the AI system prompt requires every Markdown table and
` ```mermaid``` ` block be preserved verbatim, so this applies to the architecture and API-reference pages
too, not just the purely narrative ones), configure one AI provider via either config layer above:
`Ai:AzureFoundry:*` (preferred if set) or `Ai:GitHubToken` (a free GitHub Models PAT, fine-grained,
*Models: read-only*). Azure Foundry's `Endpoint` is the deployment's `.../openai/v1` base URL (found on the
deployment's "Get code" panel in the Foundry portal), and `Deployment` is the model deployment name (not
necessarily the underlying model name). See **AI reliability** above for retry/failover/TPM tuning.

### Knowledge Store (persisted)

The Knowledge Model itself — the graph of nodes and relationships — is a **versioned, append-only**
store, independent of the document store below. Each commit appends a new immutable `Snapshots` row (SQL
Server, same `AipHistory` database as Run History — see **Run History** above); snapshots are never
mutated, and nodes/relationships are serialized as JSON columns per row (`EfKnowledgeRepository`,
`src/Aip.Infrastructure/RunHistoryStore.cs`). A snapshot committed in one run is the real baseline for the
next, across process restarts *and* across machines/containers — which is what makes incremental analysis
and `skipIfUnchanged` work for real in a standalone/unattended deployment, not just within one long-lived
process on one box. `IKnowledgeRepository` (`src/Aip.Core/Abstractions`) is a Core PORT specifically so
this storage choice can change again later (e.g. to a dedicated graph database) without touching the Core.

### Document store (versioned)

Every run that finds real changes publishes a new, additive **documentation version** to the durable
**document store** (`IDocumentStore`) — this is the one and only place `Aip.Viewer` reads from. Writing
here never touches a source-controlled repository (neither AIP's own nor any analyzed repo), so no repo
grows as more applications are documented. Two implementations, selected by configuration:

- **Filesystem (default)** — writes under `<AIP_OUTPUT>/documents/<app>/v<N>/…`. Override the root with
  `AIP_DOCS_ROOT`.
- **Azure Blob Storage** — set `Storage:ConnectionString` (or `AIP_BLOB_CONNECTION_STRING`) and it takes
  over automatically; the pipeline code never changes.

Publishing is purely additive — a run never clears or overwrites a prior version. Each version's pages
live under their own `v<N>/` prefix, and an app-level `_versions.json` index (also just an ordinary
document in the store) tracks every version's number, creation time, per-repository commit(s), and page
count; "latest" is always computed as the highest version number, never stored redundantly. A run whose
`skipIfUnchanged` check finds nothing changed publishes no new version at all. Historical Knowledge Model
*facts* (as opposed to rendered documentation) live in the separate Knowledge Store described above — the
two stores are deliberately independent concerns.

## Deployment

The intended production shape is **standalone and unattended** — no CI/CD pipeline in the loop, just the
platform itself running as a normal app and something external calling it once a day.

**Build the image** (`Dockerfile`, repo root):

```bash
docker build -t aip-host .
```

The final image stays on the full .NET **SDK** base (not the slimmer `aspnet` runtime image most ASP.NET
Core containers use) because `Aip.Engines.Roslyn` needs a real MSBuild toolset available at *runtime* —
`Microsoft.Build.Locator.RegisterDefaults()` scans the running machine for an installed SDK every time it
analyzes a target repo's `.csproj`/`.sln` files, not just once at this image's own build time. `git` is
installed explicitly (not present in the base image) since `GitRepositorySource` shells out to it directly.
`Aip.slnx` + `apps.yml` + the committed `appsettings.json` ship inside the image (no secrets in any of
them — see **Configuration** above); real connection strings and credentials are supplied as environment
variables at deploy time, never baked into a layer.

**Run it** — the container's entrypoint is `serve --config apps.yml` (see **Run** above), listening on
port 8080:

```bash
docker run -p 8080:8080 \
  -e AIP_SQL_CONNECTION_STRING="..." -e Logging__ConnectionString="..." \
  -e AIP_GITHUB_TOKEN="..." -e Run__Secret="..." \
  aip-host
```

**Suggested Azure shape**: an **Azure Container App** running this image with HTTP ingress and
`minReplicas: 0` — Container Apps' built-in HTTP-triggered scale-to-zero means it costs nothing while idle
and wakes up automatically on the first request, which fits a job that only genuinely needs to do work
once a day. A small **Azure Function on a Timer Trigger** (`serve` needs nothing pipeline-shaped calling
it) does the actual triggering — its entire body is one `POST /run` call to the Container App, with
`X-Run-Key` set to the same value as `Run:Secret`/`AIP_RUN_SECRET`; it contains no analysis logic itself,
so none of the concerns above (MSBuild toolset, `git`, long execution time) apply to it — only to the
Container App doing the real work.

---

## Notes for contributors

- The `verify` and `demo` runners live in the Host and contain no platform logic — they exercise and
  demonstrate the real subsystems.
- New technologies enter as **plugins**, new outputs as **projections**, new vocabulary via the
  **Schema Registry** — never by editing the Core. A plugin's `PluginManifest.Capabilities` list is the
  single source of truth for which node kinds it may legitimately emit; keep it in sync with what your
  analyzers actually emit, since `ValidationPipeline` checks every accepted node's kind against the
  union of every loaded plugin's declared capabilities (plus the fixed `CoreNodeKinds` set — currently
  just `External`, see `Aip.Core.Domain.Vocabulary`) and surfaces a warning diagnostic (never a
  rejection) for anything unrecognized.
- Any improvement idea that would touch a frozen decision is recorded as a **future ADR**, not applied.
