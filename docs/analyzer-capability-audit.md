# AIP Roslyn Analyzer — Capability Audit

What the analyzer actually detects today, area by area — not what Roslyn could theoretically support.
Every row below is backed by a specific class, method, and technique read directly from the current
source (`src/Aip.Plugins.AspNetCore`, `src/Aip.Plugins.Angular`, `src/Aip.Plugins.React`,
`src/Aip.Plugins.NextJs`, `src/Aip.Plugins.Security`, `src/Aip.Knowledge/RelationshipResolution.cs`,
`src/Aip.Infrastructure/Scanning.cs`, `src/Aip.Engines.Roslyn`, `src/Aip.Engines.TypeScript`,
`src/Aip.Projections/Documentation.cs`). No capability below is inferred from naming or intent — each
was confirmed present or absent by reading the implementing code.

Legend: ✅ Fully Supported · 🟡 Partially Supported · ❌ Not Supported.
"Gap type" is either **Implementation gap** (Roslyn/the toolchain can do this, we just haven't built it)
or **Roslyn limitation** (genuinely not resolvable through this kind of static analysis).

---

## Verdict — Database Interaction Model per execution path: **not built**

You asked directly whether we'd extended the analyzer to record every database interaction — entity,
operation type, method, LINQ shape, transactions, raw SQL — and attach it to each execution path. **No.**
What exists today (`TechnologyUsageAnalyzer`) is a flat, per-**class** bag of method names, not a
per-**path** model. It cannot currently produce this:

```
POST /orders
→ CreateOrderCommand
→ CreateOrderHandler
→ CustomerRepository
→ READ Customers
→ INSERT Orders
→ INSERT OrderItems
→ SaveChangesAsync
```

The `Endpoint → Command → Handler → first dependency` half of that chain **was** built this session
(§3, §10) — `DISPATCHES` / `HANDLES` / `DEPENDS_ON`. What's missing is everything past that: operation-type
classification, LINQ operator tracking, transactions, raw SQL / stored procedures, `AsNoTracking`,
pagination, aggregations — and, even where a fact is captured today, it's never tied to a specific call
site or execution path. Full breakdown in [§17](#17--database-interaction-model--the-specific-ask).

---

## 1 · Solution & Project Analysis

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Solution structure (.sln/.slnx) | 🟡 Partial | `RepositoryScanner.DiscoverSolutionProjects` — regex over classic `.sln` `Project()` lines; `XDocument` parse of `.slnx` `<Project Path="">`. Falls back to globbing every `.csproj` when no solution file exists. | Only used to scope artifact discovery — never modeled as a Solution node itself (no solution folders/build configs). | Implementation gap |
| Projects | ✅ Full | Each `.csproj` (scoped by the solution when present) becomes a Project artifact/node — name, kind, target framework. | None material. | — |
| Project references | ❌ None | Nothing — `<ProjectReference>` elements are never read; no Project→Project edge exists anywhere. | Cross-project links only exist indirectly via type-level DEPENDS_ON/IMPLEMENTS. | Implementation gap |
| NuGet packages | 🟡 Partial | `PackageAnalyzer` reads `<PackageReference Include="...">`, matched against ~48 fragments → Technology node. | The `Version` attribute is never captured. | Implementation gap |
| Technology/framework detection | ✅ Full | Two layers: presence (`PackageAnalyzer`) + real usage detail via semantic symbol resolution (`TechnologyUsageAnalyzer`), sharing one `CapabilityRules` table. | Only covers the ~48 tracked fragments (generic `Azure.*`/`AWSSDK.*` catch-alls exist). | — |

## 2 · ASP.NET Core

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Controllers | ✅ Full | Semantic BaseType-chain walk for `ControllerBase`/`Controller` + `[ApiController]` (semantic w/ syntactic fallback); name-suffix fallback at lower confidence. | None material. | — |
| Minimal APIs | 🟡 Partial | `MapGet/Post/Put/Delete/Patch`, route groups (`MapGroup`), "endpoint-group classes" (`IEndpointGroup`/`EndpointGroupBase`, hardcoded `/api/{ClassName}` prefix). | **Purely syntactic** — any method literally named `Map*` matches, no semantic check the receiver is `IEndpointRouteBuilder`. | Implementation gap |
| Routes | 🟡 Partial | Real `[controller]`/`[action]` token substitution, template combination, MVC fallback. | Route constraints (`{id:int}`) are stripped, not parsed — the type/value is discarded. | Implementation gap |
| HTTP verbs | ✅ Full | Get/Post/Put/Delete/Patch, syntactic attribute/method-name matching. | Custom verbs (`[HttpHead]`, `[AcceptVerbs]`) not covered. | — |
| Parameters | ❌ None | **Confirmed absent.** No analyzer reads parameter names/types/`[FromBody]`/`[FromQuery]`/`[FromRoute]`/`[FromServices]` anywhere. | Endpoint nodes only carry verb/route/action/authorize. | Implementation gap |
| Return types | ❌ None | **Confirmed absent.** Zero occurrences of `method.ReturnType` inspection anywhere. | Trivial via `IMethodSymbol.ReturnType` — just not wired up. | Implementation gap |
| Authorization | ✅ Full | `[Authorize]`/`[AllowAnonymous]`, Roles/Policy resolved through named constants (not just literals), inherited from a shared base controller. | Minimal-API `RequireAuthorization` policy name is literal-only (no constant resolution). | — |
| Filters | 🟡 Partial | Classes implementing 1 of 9 filter interfaces (semantic). | `[TypeFilter]`/`[ServiceFilter]` and global `MvcOptions.Filters` registrations aren't detected. | Implementation gap |
| API versioning | ❌ None | **Confirmed absent.** Zero references anywhere; `Asp.Versioning.*` isn't even in the package table. | Everything. | Implementation gap |

## 3 · CQRS / MediatR

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Commands | ✅ Full | `IRequest`/`IRequest<T>` classified via namespace text → name suffix → result-type fallback. | Heuristic classification can mislabel unconventional naming. | — |
| Queries | ✅ Full | Same mechanism, opposite branch. | Same heuristic risk. | — |
| IRequest | ✅ Full | Semantic `AllInterfaces` match, arity-agnostic. | None material. | — |
| IRequestHandler | ✅ Full | `IRequestHandler<TReq,TRes>` → Handler node + HANDLES relationship. | None material. | — |
| Notifications | ✅ Full | `INotification`/`INotificationHandler<T>`, same mechanism as request handlers. | Modeled as generic "Event" — no Domain vs Integration distinction (§11). | — |
| Validators | ✅ Full | `AbstractValidator<T>` + real `RuleFor(...)` constraint content (NotEmpty, MaximumLength(50), custom codes/messages). | Rich detail captured but **never rendered** in generated docs (§16). | — |
| Pipeline Behaviors | ❌ None | **Confirmed absent.** Zero matches for `IPipelineBehavior` anywhere. | Cross-cutting steps (validation, logging, transactions-as-behavior) are entirely invisible. | Implementation gap |
| MediatR `Send()` detection | ✅ Full | `MediatorDispatch` (built this session) — semantic: gates on the receiver's resolved type being `IMediator`/`ISender`, resolves the argument's static type, confirms it implements `IRequest`. | Method-group (delegate-reference) minimal-API handlers aren't walked, only inline lambdas — disclosed scope limit. | — |
| `Publish()` detection | ❌ None | **Confirmed absent.** `MediatorDispatch` only matches the method name `"Send"`. | A `mediator.Publish(new OrderCreatedEvent())` notification dispatch is invisible. | Implementation gap |
| Controller → Command/Query mapping | ✅ Full | New this session — `DISPATCHES` relationship, wired into both `ControllerAnalyzer` and `MinimalApiAnalyzer`. | None material. | — |
| Command/Query → Handler mapping | ✅ Full | `HANDLES` relationship (pre-existing, `CqrsAnalyzer`). | None material. | — |
| Complete request execution flow reconstruction | 🟡 Partial | `Endpoint → Command/Query → Handler → first dependency`, rendered as a mermaid sequence diagram (this session). | Stops one hop past the handler; only the FIRST constructor dependency shown; pipeline behaviors invisible. | Implementation gap |

## 4 · Dependency Injection

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Constructor injection | ✅ Full | `DEPENDS_ON` — fully semantic, walks every constructor parameter's in-source type. | Class-level only (see §10). | — |
| Interface → Implementation mapping | 🟡 Partial | `AddScoped/AddSingleton/AddTransient<IFoo, Foo>()` (exactly 2 type args), resolved semantically into IMPLEMENTS. | Implementation kind decided by a crude `EndsWith("Repository")` check — everything else defaults to "Service". | Implementation gap |
| Service registrations | ✅ Full | AddScoped/AddSingleton/AddTransient recognized as DI calls. | See Lifetime detection. | — |
| Lifetime detection | ❌ None | **Confirmed absent.** The matched lifetime word is used only as a filter, never captured as a property. | All three lifetimes collapse into the identical fact. | Implementation gap |
| Factory registrations | ❌ None | **Confirmed absent.** `AddScoped<IFoo>(sp => ...)` is single-type-arg, explicitly excluded by the "exactly 2" guard. | A very common registration shape is invisible. | Implementation gap |
| Open generics | ❌ None | **Confirmed absent.** `AddScoped(typeof(IRepository<>), typeof(Repository<>))` isn't a `GenericNameSyntax` call, doesn't match the pattern at all. | Common generic-repository pattern invisible. | Implementation gap |

## 5 · Services & Business Layer

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Services | 🟡 Partial | Classes named `*Service` or implementing a `*Service`-named interface. | Purely a naming convention — nothing about actual responsibility inspected. | Implementation gap |
| Interfaces | ✅ Full | Every interface + IMPLEMENTS/EXTENDS for in-source types, fully semantic. | None material. | — |
| Service dependencies | ✅ Full | Via DEPENDS_ON. | Class-level, not method-level. | — |
| Cross-service calls | ❌ None | **Confirmed absent.** DEPENDS_ON only proves A *could* call B (injected) — never that a specific method actually invokes another. No general method-body call-target resolution exists. | Everything at the method level. | Implementation gap |
| Method invocation chains | ❌ None | Same reasoning — no general call-graph walker to chain from. | Everything. | Implementation gap |

## 6 · Entity Framework Core

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| DbContexts | ✅ Full | Classes inheriting `DbContext`, semantic BaseType walk. | None material. | — |
| DbSets | ✅ Full | `DbSet<T>` properties, T resolved semantically → OWNS + Entity node. | None material. | — |
| Entities | ✅ Full | Two paths: DbSet targets + a structural heuristic (public properties, no ordinary methods, excludes DTO/Service/Controller/DbContext-named types) — recovers rich domain entities too. | None material. | — |
| Relationships | 🟡 Partial | `HasOne`/`HasMany` fluent-chain walking (generic arg or nav-property lambda) → REFERENCES/HAS_MANY, plus implicit navigation-property heuristic. | Cardinality not fully captured (HasOne always → REFERENCES regardless of WithOne vs WithMany); no FK column name; `[ForeignKey]`/data-annotation config not parsed at all. | Implementation gap |
| Fluent API | 🟡 Partial | `HasOne`/`HasMany` only. | `HasIndex`, `HasKey`, `Property(...).HasColumnName/HasConversion`, `ToTable`, etc. not parsed — confirmed by absence. | Implementation gap |
| Migrations | ❌ None | **Confirmed absent.** Zero matches anywhere. | Migration classes are ordinary C# fully visible to Roslyn — purely unbuilt. | Implementation gap |
| Repository usage | 🟡 Partial | That a class IS a repository (name/interface suffix) + who depends on it. | What a repository method actually DOES (entity, operation) not captured at all. | Implementation gap |
| SaveChanges detection | ❌ None | Not a distinct concept — only ever appears incidentally in `TechnologyUsageAnalyzer`'s flat, capped bag with no meaning attached. | Dedicated detection tied to the execution path (see §17). | Implementation gap |

## 7 · Database Access

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Repository pattern | 🟡 Partial | Nominal detection only (see §6). | No operation-level detail. | Implementation gap |
| EF Core queries | ❌ None | No Read/Insert/Update/Delete classification, no per-query detail anywhere. | See §17. | Implementation gap |
| LINQ | ❌ None | **Confirmed absent.** `Where`/`Select`/`Join`/`GroupBy`/`OrderBy`/`Skip`/`Take`/`Distinct` are `System.Linq.Queryable` methods — not in the tracked-fragment table, filtered out entirely, not even captured incidentally. | Everything. | Implementation gap |
| Raw SQL | ❌ None | **Confirmed absent.** Zero matches for `FromSqlRaw`/`FromSqlInterpolated`/`ExecuteSqlRaw`/`ExecuteSqlInterpolated`. | Roslyn sees these call sites trivially — purely unbuilt. | Implementation gap |
| Dapper | 🟡 Partial | Presence via `using Dapper` import; incidental method-name capture via `TechnologyUsageAnalyzer` (Dapper IS in the capability table). | No SQL text, no parameter capture, no per-call-site record. | Implementation gap |
| Stored procedures | ❌ None | **Confirmed absent.** No `EXEC`/`CommandType.StoredProcedure` detection anywhere. | Everything. | Implementation gap |

## 8 · Frontend (Angular)

> **Engine ceiling:** every Angular/React/Next.js analyzer runs **regex over raw text** — `TypeScriptSemanticModel.Parser` is literally `"heuristic"`. There is no Roslyn-equivalent TS AST parser wired in; zero type binding, zero symbol resolution. This ceiling applies to every row below. **Roslyn limitation** (a real AST parser would need a Node.js sidecar, e.g. ts-morph — noted as a known future upgrade in the engine's own doc comment).

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Components | ✅ Full | `@Component(...)` + selector + templateUrl → child composition (RENDERS) via template resolution. | Regex-based, brittle against unusual formatting. | — |
| Services | ✅ Full | `@Injectable(...)` classes. | None material. | — |
| Routing | 🟡 Partial | Every `path:` string in a file whose name/content suggests routing. | No `loadChildren` (lazy-loading) handling at all; no parent/child nesting reconstructed. | Implementation gap |
| Modules (@NgModule) | ❌ None | **Confirmed absent.** Only module CONTENTS captured, never the module container. | Everything. | Implementation gap |
| Standalone Components | ❌ None | **Confirmed absent.** No `standalone: true` detection. | A property flag. | Implementation gap |
| Guards | ✅ Full | Class-based (`CanActivate`/`CanDeactivate`/`CanMatch`) and functional guards, plus HttpInterceptor as a distinct kind. | None material. | — |
| Resolvers | ❌ None | **Confirmed absent.** `Resolve`/`ResolveFn` isn't in the pattern set at all. | Same regex treatment as guards, extended. | Implementation gap |
| Reactive Forms | ❌ None | **Confirmed absent.** Zero matches for `FormGroup`/`FormBuilder`/`FormControl` (React has an equivalent analyzer; Angular does not). | A form/field/validator analyzer. | Implementation gap |
| HttpClient calls | 🟡 Partial | `this.http.get/post/...` with URL resolved for literals + same-file `this.field` references. | Can't resolve cross-file constants (`environment.apiUrl`), multi-hop variables, or `+` concatenation. | Roslyn limitation (needs real cross-module resolution) |
| State management | ❌ None | **Confirmed absent.** Zero matches for `BehaviorSubject`/NgRx/Akita/`signal(`. | Everything. | Implementation gap |

## 9 · Frontend → Backend Flow

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| API endpoint matching | ✅ Full | Exact verb+normalized-route match (0.9 confidence); suffix match ONLY when exactly one candidate qualifies (0.75); otherwise explicitly left unresolved. Route normalization collapses `${id}`/`{id}`/`:id` to one placeholder shape. | Deliberate ambiguous-match refusal — a correct design choice, not a gap. | — |
| HttpClient → Controller mapping | ✅ Full | Works **across two separate repos** in the same Application — `CrossRepositoryDependencyResolver` re-derives cross-repo matches into repo-level DEPENDS_ON. | Inherits the frontend URL-extraction ceiling. | — |
| End-to-end request flow | 🟡 Partial | Component → ApiCall → Endpoint fully resolved, now extends into Command → Handler → first dependency. | Stops before reaching the actual database operation. | Implementation gap |

## 10 · Call Graph

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Method call graph | ❌ None | **Confirmed absent.** No general "method A calls method B" fact recorded anywhere — only class-level DEPENDS_ON plus hand-built special cases (mediator Send, broker Publish, notification Send*, frontend API calls). | A general invocation-expression walker at the method level. | Implementation gap |
| Cross-project calls | 🟡 Partial | Type-level facts (DEPENDS_ON/IMPLEMENTS/EXTENDS/MAPS_TO) resolve across projects via a solution-wide Roslyn Compilation. | Still class-level, not method-call level. | Implementation gap |
| Recursive flow analysis | 🟡 Partial | One real BFS exists — `Documentation.cs`'s `RoleBackendAccess` (Role → Route → components → ApiCall → Endpoint) at projection time. | Purpose-built for one question, not a general-purpose traversal engine. | Implementation gap |
| Controller → Service → Repository chain | 🟡 Partial | Walkable via class-DEPENDS_ON for classic (non-CQRS) architectures; visible in the unfiltered Component-relationships graph. | No dedicated "resolve this controller's chain" feature — implicit only. | Implementation gap |
| Controller → MediatR → Handler → Repository chain | 🟡 Partial | Explicitly reconstructed and rendered (this session) — real, tested. | Only 1 hop past the handler, only the first dependency, no entity/operation detail once it reaches the repository. | Implementation gap |

## 11 · Event-Driven Architecture

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Domain Events | ❌ None | No distinction from Integration Events — both are the same generic "Event" kind. | A namespace/naming classifier, the same way Command vs Query works. | Implementation gap |
| Integration Events | ❌ None | `IIntegrationEventHandler` recognized as a Consumer-qualifying interface, but produces the identical kind as every other consumer interface. | Distinct kind or property. | Implementation gap |
| MediatR Notifications | ✅ Full | See §3. | — | — |
| Event Bus / Azure Service Bus / Kafka / RabbitMQ | 🟡 Partial (broker presence ✅) | Broker identity (namespace-import gated), Publisher→Message and Message→Consumer edges (message keyed by argument TYPE, not topic/queue name), boolean DLQ flag from text markers. | No exchange/topic/queue NAME ever extracted. | Implementation gap |

## 12 · Background Processing

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Hosted Services / BackgroundService | ✅ Full | `IHostedService`/`BackgroundService`, semantic. | None material. | — |
| Hangfire | ✅ Full | `AddHangfire(...)` call + package reference. | Job definitions (`RecurringJob.AddOrUpdate`) not parsed. | Implementation gap |
| Quartz | ✅ Full | `AddQuartz(...)` call + package reference. | Job/trigger definitions not parsed. | Implementation gap |
| Azure Functions | ✅ Full | `[Function]`/`[FunctionName]` + trigger attribute, both hosting models. | None material. | — |
| Timers | ❌ None | **Confirmed absent.** Zero matches for `Timer`/`PeriodicTimer`/cron patterns. | Everything — ordinary calls Roslyn sees trivially, purely unbuilt. | Implementation gap |

## 13 · Security

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| Authentication | 🟡 Partial | Scheme registration calls (`AddJwtBearer`, `AddCookie`, `AddOpenIdConnect`, Okta/Auth0 helpers) — syntactic. | Presence-only, no configuration detail. | Implementation gap |
| Authorization | 🟡 Partial | `AddAuthorization` presence + `[Authorize(Roles=/Policy=)]`/`[AllowAnonymous]` usage sites (with constant resolution). | Actual policy DEFINITIONS (`AddPolicy`/`RequireClaim`/`RequireRole`) never parsed — only the name reference. | Implementation gap |
| Policies | ❌ None | Only the name referenced, never what it requires. | Everything past the name. | Implementation gap |
| Roles | ✅ Full | Role names from `[Authorize(Roles=...)]`, including named constants. | None material. | — |
| Claims | ❌ None | **Confirmed absent.** No `ClaimsPrincipal`/`RequireClaim` detection. | Everything. | Implementation gap |
| JWT | 🟡 Partial | Scheme presence only. | No `TokenValidationParameters`/issuer/audience parsing. | Implementation gap |
| Custom IAuthorizationHandler | ❌ None | **Confirmed absent.** Zero matches for `IAuthorizationHandler` anywhere. | Everything. | Implementation gap |

## 14 · Configuration

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| appsettings.json | 🟡 Partial | Only the `ConnectionStrings` object; top-directory only. | The rest of the config tree is never read. | Implementation gap |
| Options Pattern | ❌ None | **Confirmed absent.** Zero matches for `IOptions`/`services.Configure<T>`. | Everything. | Implementation gap |
| IConfiguration | ❌ None | **Confirmed absent.** No direct `configuration["Key"]`/`.GetValue`/`.GetSection` usage tracked. | Everything. | Implementation gap |
| Environment Variables | ❌ None | **Confirmed absent.** No `Environment.GetEnvironmentVariable` detection. | Everything. | Implementation gap |
| Feature Flags | ❌ None | **Confirmed absent.** Not even in the technology table. | Everything. | Implementation gap |

## 15 · External Integrations

| Feature | Status | Detected / Mechanism | Limitations / Missing | Gap type |
|---|---|---|---|---|
| REST APIs (outbound) | ❌ None | **Confirmed absent.** No `HttpClient`/`IHttpClientFactory` tracked as an outbound backend call (only Angular's frontend-only analyzer exists). | An outbound-call analyzer mirroring the frontend one. | Implementation gap |
| GraphQL | ❌ None | Zero matches anywhere. | Everything. | Implementation gap |
| gRPC | ❌ None | Zero matches anywhere. | Everything. | Implementation gap |
| SignalR | ❌ None | Zero matches anywhere (one unrelated code comment only). | Everything. | Implementation gap |
| Redis | ✅ Full | `AddStackExchangeRedisCache`/`AddRedis` + package + usage detail. | None material. | — |
| Blob Storage | 🟡 Partial | Package reference + incidental usage via `TechnologyUsageAnalyzer`. | No dedicated `BlobServiceClient` analyzer, no container/blob-name extraction. | Implementation gap |
| SMTP | 🟡 Partial | Name-shape heuristic only (`Send*` on an `email`/`notif`/`mail`-named receiver) — zero real `SmtpClient` detection. | Real email-library detection. | Implementation gap |
| Azure SDKs | ✅ Full | ~15 specific services + generic `Azure.*` catch-all. | None material. | — |
| AWS SDKs | 🟡 Partial | S3 specifically, SQS/SNS via messaging, generic `AWSSDK.*` catch-all. | Far fewer specific rows than Azure. | Implementation gap |

## 16 · Documentation Coverage

| Feature | Status | Notes |
|---|---|---|
| Facts that DO surface in docs | ✅ Full | Architecture (layers, patterns, components, request-flow diagrams, status workflows), API Reference, Technology Stack, Data & Storage, Frontend (roles/routes/journeys/screens/components), Security, Cross-cutting concerns. |
| Facts captured but **never rendered** | ❌ None | `BusinessRule` nodes and `AuditLog`/`AUDITS` relationships are fully captured but appear on **zero** generated pages — confirmed by absence from every section builder in `Documentation.cs`. Validator `RuleFor(...)` detail is captured but only the validator's *name* surfaces — the actual rules never make it into prose. |
| Systemic AI-rewrite content-preservation gap | 🟡 Partial | The AI rewrite step needs an explicit "preserve verbatim" instruction per template or it paraphrases/drops deterministic content — fixed this session for Vulnerabilities and the new mermaid diagrams. `tech-frontend-screens`/`tech-frontend-components` were never audited for the same risk. |

---

## 17 · Database Interaction Model — the specific ask

Your proposed capability, itemized against the current implementation. This is new scope, not a re-audit
of §6–7 — it maps your exact list to what the analyzer would need to gain.

| Item from your spec | Status | Current state | What's needed | Gap type |
|---|---|---|---|---|
| Entity/Table involved | ❌ None | Not captured per call site. `TechnologyUsageAnalyzer.InferSubject` already resolves the right entity type for a call (e.g. `DbSet<Contract>` → `Contract`) — the building block exists, just never captured as a structured field per query. | A record per call site: `{entity, method, callerClass, callerMethod}`. | Implementation gap |
| Operation type (Read/Insert/Update/Delete/Upsert) | ❌ None | No method-name → operation-type classification exists (`Add`→Insert, `Remove`→Delete, `FirstOrDefaultAsync`→Read, etc.). | A ~30-row lookup table + wiring. | Implementation gap |
| Method/API used (Add, FirstOrDefaultAsync, ExecuteUpdate, FromSqlRaw, Dapper Query, ...) | 🟡 Partial | The method NAME is already captured by `TechnologyUsageAnalyzer`, flattened and capped per class, not per call site. | Un-flatten into per-call-site records. | Implementation gap |
| LINQ operators (Where, Select, Join, Include, ThenInclude, GroupBy, OrderBy, Skip, Take, Distinct) | ❌ None | Standard `System.Linq.Queryable` methods aren't in the tracked-fragment table at all — filtered out before inference even runs. | Add `System.Linq(.Queryable/.Async)` as a tracked fragment; capture the operator chain, not just the terminal call. | Implementation gap |
| SaveChanges / SaveChangesAsync | ❌ None | Not a distinct fact (§6). | Dedicated detection tied to which entities were touched earlier in the same method/handler. | Implementation gap |
| Transactions (BeginTransaction, Commit, Rollback) | ❌ None | Zero matches for `BeginTransaction`/`Database.BeginTransactionAsync`/`TransactionScope` anywhere. | Everything. | Implementation gap |
| Bulk operations (ExecuteUpdate, ExecuteDelete, bulk-insert libraries) | ❌ None | EF Core 7+'s `ExecuteUpdate`/`ExecuteDelete` and bulk-extension libraries aren't referenced anywhere. | Everything. | Implementation gap |
| Raw SQL / stored procedures | ❌ None | See §7. | Everything. | Implementation gap |
| AsNoTracking | ❌ None | Not detected. | Trivial once any query-shape analyzer exists — a single well-known method name. | Implementation gap |
| Async vs Sync | 🟡 Partial | Not explicit, but recoverable for free — the exact method name (`FindAsync` vs `Find`) is already captured. | A one-line `.EndsWith("Async")` classification when building the structured record. | Implementation gap |
| Pagination (Skip/Take) | ❌ None | Not tracked — same LINQ-operator gap. | Same LINQ-operator work as above. | Implementation gap |
| Aggregations (Count, Sum, Average, Min, Max) | ❌ None | Not tracked — same LINQ-operator gap. | Same LINQ-operator work as above. | Implementation gap |
| **Attaching every operation to its execution path** | ❌ None | The hardest piece — doesn't exist in any form. Requires walking `Endpoint → (DISPATCHES) → Command → (HANDLES) → Handler → (DEPENDS_ON, all hops) → Repository/DbContext method body → the operations inside it`. | The Endpoint→Command→Handler→first-dependency chain built this session is the **first three hops** of exactly this walk — it just doesn't yet reach into the dependency's own method bodies. | Implementation gap |

---

*Compiled by reading the working tree directly — no capability above is inferred from naming or intent.*
