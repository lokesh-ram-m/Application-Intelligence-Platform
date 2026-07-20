# Application Intelligence Platform — Backlog

Deliberately-deferred work, grouped by area. The architecture (analyzers behind `IAnalyzer` in
technology **plugins**, engines behind `ILanguageEngine`, cross-cutting resolvers in the Relationship
Resolution Engine, AI behind `IAiProvider`) is designed so each of these drops in without touching the
Core. New technology enters as a **plugin**, new output as a **projection**, new vocabulary via the
**Schema Registry** — never by editing the Core.

## Platform / engine (structural — biggest fidelity wins)

- ~~**Cross-project call graph**~~ — **done (default on).** A **solution-wide Roslyn compilation** resolves
  symbols across the whole `.sln`, so a `DEPENDS_ON` from a controller in `Web` to an `IService` in `Core`
  now connects (previously dropped, since identities are project-scoped). Validated fast: EntityManagement
  (8 projects) ~2s / +22 relationships; eShop (24 projects) ~8s. Cross-project types dedupe to one node.
  Guard rails: skip EF migrations/generated code, bounded `.sln` walk, ≤1200-file-per-scope cap. Disable
  with `AIP_CROSS_PROJECT=0`. **Longer term:** use MSBuild project references instead of an ad-hoc source
  dump for precise per-project reference graphs.
- ~~**Persisted knowledge store**~~ — **done, since superseded.** Originally a JSON-file-per-app store
  (`.aip-store/<app>.json`); now `EfKnowledgeRepository` (SQL Server) — see the "Knowledge graph → a real
  DB" entry further down for the current shape. Either way, a snapshot committed in one run is the
  baseline for the next, so incremental carry-forward holds **across processes and machines** (verified by
  `Snapshot_persists_across_store_instances`).
- **ts-morph sidecar for TypeScript.** The TS engine reads source with heuristics (regex); there is no
  Roslyn-equivalent AST parser for TS on .NET. A Node sidecar running **ts-morph** would give a real AST
  (accurate types, imports, DI graph, templates). `TypeScriptSemanticModel.Parser` already carries the
  provenance so facts stay honest about which parser produced them.

## Architectural intelligence (cross-cutting concerns — detect the design, not just the types)

Detected **generically** (via framework APIs, not repo-specific names) and **semantically** (by contract, not
by naming). Phase 1 shipped:

- ✅ **Caching** — in-memory / distributed / Redis (`AddMemoryCache`, `AddStackExchangeRedisCache`, …).
- ✅ **Auth schemes** — JWT, Cookie, **Entra ID/Azure AD**, OIDC (`AddJwtBearer`, `AddMicrosoftIdentityWebApi`, …),
  plus inherited base-controller `[Authorize(Roles=…)]`.
- ✅ **CORS**, **health checks**, **background jobs** (Hangfire/Quartz), **logging/audit** (Serilog/Audit.NET).
- ✅ **Middleware pipeline** — ordered `app.Use…()` stages.
- ✅ **CQRS** — semantic via `IRequest`/`IRequest<T>`/`IRequestHandler<,>`/`INotification`; command-vs-query by
  namespace/result type (not name); `HANDLES` links.
- ✅ **FluentValidation** — `AbstractValidator<T>` → `VALIDATES` the target.

Phase 2 shipped (deeper, still generic/semantic):

- ✅ **Messaging deep-dive — broker-agnostic.** One `MessagingAnalyzer` recognizes each transport (RabbitMQ,
  Azure Service Bus, AWS SQS/SNS, Kafka, MassTransit, NServiceBus, CAP) but emits one uniform model:
  `Type —PUBLISHES→ Message ←CONSUMES— Consumer`, a `MessageBroker` node, and a DLQ flag. New brokers = one
  table entry. Publish/consume method detection is gated on the broker SDK namespace (no false positives).
- ✅ **Data access — any provider.** Beyond EF: **Dapper** (namespace), raw ADO.NET
  (`SqlConnection`/`Npgsql`/`MySql`/`Sqlite`/`Oracle`), **MongoDB** (`IMongoCollection`/`MongoClient`) →
  `DataAccess` nodes with approach + database.
- ✅ **Filters** — `IAuthorizationFilter`/`IActionFilter`/`IExceptionFilter`/`IResultFilter`/`IEndpointFilter`
  by contract → `Filter` nodes with kind.
- ✅ **DataAnnotations** — `[Required]`/`[MaxLength]`/`[Range]`/… flag validated fields on entities.
- ✅ **Resilience** — Polly (`AddResilienceHandler`/`AddPolicyHandler`).

Remaining:

- **Idempotency** — Redis-backed idempotency keys (harder to detect generically).
- **Per-field validation rules** — capture the actual rule (max length, range bounds), not just which fields.
- **DataAnnotations on DTOs** — today annotations are flagged on entities; extend to request/response models.

## Backend extraction (.NET / ASP.NET Core plugin)

- **CQRS modeling.** MediatR is detected as a *technology*; model `*Command` / `*Query` / `*Handler`
  types as first-class nodes (and link endpoints → handlers → commands).
- **Repository/Service → DataStore "persists-to" edge.** Connection-string data stores are app-scoped
  (no project segment), so the `ServiceToDatabaseResolver` can't match them. Add a resolver that links
  repositories/services to the data store they persist to.
- **Nested `MapGroup` / route-param semantics.** Data-flow of group variables; `.../user/{userId}`
  should read "by user id", not the generic "by id".
- **Auth depth.** Per-endpoint roles/policies/fallback policies beyond the basic `[Authorize]` label.
- ~~**Test-project filtering.**~~ — **done.** Projects referencing xunit/nunit/MSTest/Test.Sdk are marked
  `kind: test` on the `Project` node, so they read distinctly from app projects in the solution structure.
- **Generic type resolution** — `Task<Result<T>>`, `Repository<T>` via the semantic model.

## Frontend extraction (Angular plugin)

- **Component → API-call wiring.** Link a component/service to the specific `ApiCall` it makes (today we
  link component → service via constructor injection; the call itself is a separate node).
- **NgModule vs standalone, providers, lazy routes.** Detect `loadChildren`/`loadComponent`, module
  boundaries, and provider graphs; label the `**` wildcard route.
- **Component detail** — `@Input` / `@Output`, template bindings.
- **Base-URL resolution across files** — resolve `environment.apiUrl` and cross-file base fields (today
  we resolve same-file `apiUrl` fields).

## More languages (new plugins/engines)

- ~~**React / Next.js**~~ — **done.** `Aip.Plugins.React` (components with props/hooks/rendering, custom
  hooks, fetch/axios calls, context) and `Aip.Plugins.NextJs` (reuses React analyzers + file-based App/Pages
  routing) both ship on the existing TypeScript engine.
- **Vue** via the existing TypeScript engine (a new technology plugin).
- **Python / Java / Go** — new `ILanguageEngine`s + plugins to claim the projects the scanner currently
  records as unsupported.
- **Full base-URL resolution** — resolve `process.env.NEXT_PUBLIC_*` / `environment.*` bases so
  frontend→backend mappings are exact rather than suffix-matched.

## Platform surface / operations

- ~~**Document store + Document Viewer (Creator/Viewer split)**~~ — **done.** `IDocumentStore` port
  (`Aip.Abstractions.Documents`) with `FileSystemDocumentStore` (default) and `AzureBlobDocumentStore`
  (`Aip.Infrastructure.AzureBlob`, config-selected). `ExecutionPipeline` writes every page through it
  (clear-then-rewrite, so regenerates never leave orphaned pages), plus a per-app `_manifest.json` (page
  order) and a shared `_applications.json` index (so a landing page can list every documented app).
  `Aip.Viewer` is a standalone ASP.NET Core app that reads **live, on every request** — no caching, no
  local materialization — renders markdown via Markdig with a nav sidebar built from the same store.
  Docusaurus (the local-site generator that predated this) has been fully removed: `IDocumentationPublisher`,
  `DocusaurusPublisher`, and all `output/docs-site` generation are gone. Verified end-to-end against the
  real Azure Blob storage account. Next: `@Input`/`@Output`-style page polish, search, and the "list all
  apps" index growing stale if the store is edited outside AIP (acceptable for now — Creator is the only writer).
- ~~**Azure DevOps CI/CD trigger flow / webhook trigger endpoint**~~ — **explicitly rejected.** The
  platform runs standalone and unattended instead: `apps.yml` auto-diffed against each repo's last
  analyzed commit on a daily cadence (`Aip.Host`'s `serve` mode, `POST /run`), with no pipeline or webhook
  in the loop at all. `ITriggerAdapter`/`CliTriggerAdapter`/the CI/CD `--app --changed` CLI mode have been
  removed outright, not just deprioritized.
- ~~**Knowledge graph → a real DB**~~ — **done.** `EfKnowledgeRepository` (SQL Server, same `AipHistory`
  database as Run History) replaced the local-disk JSON store — see README's "Knowledge Store" section.
- **Repo → application self-declaration** — a `.aip.yml` manifest so repositories onboard themselves into
  the Application Registry instead of being declared centrally in `apps.yml`.
- **Observability / cost in the docs** — surface token usage and execution metrics as a generated page.
- **Infrastructure & Cloud projection page** — hosting/runtime/config/deployment summary (as DocPlatform has).
- **Real health checks for `serve` mode.** `GET /health` (`Serve.cs`) is currently a bare stub —
  unconditional `200 OK` regardless of whether anything actually works. Should use ASP.NET Core's real
  `Microsoft.Extensions.Diagnostics.HealthChecks` framework, at minimum verifying SQL Server connectivity
  (the one dependency `serve` mode can't function without), and should distinguish liveness (process
  alive) from readiness (dependencies reachable) — Azure Container Apps' scale-to-zero/restart decisions
  rely on this being meaningful, not a rubber stamp.
- **Top-level exception boundary in `Program.cs`.** The `try/finally` around the CLI flow only wraps the
  post-DI-build code — an exception from `Serve.RunAsync`, `DomainVerification.Run()`, or
  `BuildServiceProvider()` itself prints a raw .NET stack trace and exits with no structured logging and
  no clean message.
- **Validated CLI argument parsing.** `Program.cs` uses hand-rolled `args.Contains(...)`/a custom
  `GetOption` helper — no `--help`, no `--version`, no rejection of unknown or conflicting flags (e.g.
  `serve run demo` together silently just runs whichever mode is checked first, with no warning the rest
  was ignored). A validated parsing library (e.g. `System.CommandLine`) would close this.
- **Startup resilience for `serve` mode's SQL dependency.** If SQL Server is briefly unreachable when
  `serve` starts, `MigrateRunHistoryAsync()` just throws and the process dies — reasonable for a
  short-lived CLI run, less reasonable for a long-lived process that arguably should retry a transient
  connection blip rather than crash-loop.

## Documentation / AI

- **Per-document AI grounding** — send each page only the slice of the model it needs (token budget) and
  keep diagrams/tables deterministic (already done); extend to a configurable per-page AI on/off.
- **Azure AI Foundry / other providers** — a new `IAiProvider` (one class); the platform doesn't change.
