# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Turkish-language fintech agentic banking assistant (.NET 9) built on **Microsoft Agent Framework (MAF)**, **Model Context Protocol (MCP)**, and **Agent-to-Agent (A2A)**. Code comments, prompts, trace messages, and commit-adjacent docs (README.md, CHANGELOG.md, ANALIZ.md) are all in Turkish — match that when editing existing files or writing user-facing/prompt strings; C# identifiers mix Turkish domain terms (`HesapUzmani`, `KrediKartiUzmani`) with English framework terms.

## Target architecture: this is a plug-in agentic layer

**The purpose of this system is to add an agentic layer on top of an existing bank.** `src/Banking.BankApi` (and its `InMemoryBankStore`) is a **temporary stand-in** — it will be deleted. The end state is: deploy, set `BankApi:BaseUrl` + `BankApi:ApiKey`, set the port, point the MCP tools' paths at the real bank's endpoints, done. **Removing the fake bank must not require rewriting the flow.**

### Acceptance test — run this after any change that touches the bank boundary

```powershell
dotnet sln BankingAgents.sln remove src/Banking.BankApi/Banking.BankApi.csproj
dotnet build src/Banking.Host        # MUST succeed
dotnet sln BankingAgents.sln add src/Banking.BankApi/Banking.BankApi.csproj
```

Neither `Banking.Domain.csproj` nor `Banking.Host.csproj` may reference `Banking.BankApi.csproj`. Both carry a comment saying so — do not "fix" a build error by re-adding the reference.

### The swap surface (what changes when the real bank arrives)

| File | What changes |
|---|---|
| `src/Banking.Host/appsettings.json` → `BankApi` section | `BaseUrl`, `ApiKey`, `DefaultCustomerId` |
| `src/Banking.Mcp/Constants/BankApiPaths.cs` | The 33 endpoint paths |
| `src/Banking.Host/declarative-agents/*.yaml` → `action.path` | Money-moving endpoints per workflow |
| `Semantic/CampaignSummary`, `Semantic/FinancingSummary` | Field mapping if the bank's JSON shape differs |

Everything else — `IBankApiClient`, `BankApiOptions`, `RestWorkflowExecutor`, `BankApiToolGateway` — stays as is.

**Rules that keep this true:**
- Bank data is reached **only** through `IBankApiClient` over HTTP. Never take an in-process dependency on the bank's services or models.
- The customer id comes from `CustomerContext` (fed by `BankApi:DefaultCustomerId`), never from a bank-side constant. It has no silent default on purpose — a wrong customer id shows someone else's data without erroring.
- The listening port is derived from `Agents:SelfBaseUrl` in `Program.cs`. Do not write a port literal anywhere else; the process reaches its own MCP and A2A endpoints through that same address.
- `filePath` on the statement tools is a fake-bank concept (reading a statement from a file). It will be removed when the real endpoints are wired.

## Commands

```powershell
# Run the app (serves API + Web UI on the port from Agents:SelfBaseUrl, default 5080)
$env:OPENAI_API_KEY = "sk-..."
dotnet run --project src/Banking.BankApi   # separate terminal — the stand-in bank, port 5001
dotnet run --project src/Banking.Host

# Build everything
dotnet build BankingAgents.sln

# Manual deterministic-workflow scenario runner (no LLM calls; simulates HITL approve/reject/double-click)
dotnet run --project tools/WorkflowTest

# Load / journey test runner against a running host
dotnet run --project tools/LoadTest

# Dump reflected API surface of installed MAF/MCP/A2A assemblies to maf_api.txt
dotnet run --project tools/ApiDump
```

There is no automated test project. `tools/WorkflowTest` is a manual console scenario runner that covers the deterministic HITL workflow end-to-end without an LLM; `tools/LoadTest` drives full journeys against a running host. When validating behavior, prefer running the host and hitting `/api/chat`, `/api/diag`, `/api/tools`, or extending `tools/WorkflowTest`.

The LLM API key resolves in this order: User Secrets / `appsettings.json` (`Agents:ApiKey`) → `OPENAI_API_KEY` env var. `src/Banking.Host` has a `UserSecretsId` configured.

## High-level architecture

Request flow:

```
Browser (wwwroot/index.html) --/api/chat--> GatewayService
                                                  |
                        sessionAccessor.OpenSessionScopeAsync(...) + lease.EnterAmbientScope()   <-- state loaded ONCE
                                                  |
                                             FlowRouter (deterministic router)
                                     +------------+-------------+
                            (no active workflow)        (active workflow)
                                     |                          |
                                     v                          v
                            Orchestrator Agent         WorkflowRegistry.CreateEngineAsync
                          (AgentRuntime, A2A)                   |
                                     |                          v
                       +-------------+-------------+      WorkflowEngine
                       v             v             v     (phase state machine)
                 HesapUzmani  KrediKartiUzmani AnalizUzmani     |
                       |             |             |     Start / SlotFilling / Approval
                       +------------>+<------------+            |
                                     v                          v
                              Banking.Mcp (ToolRegistry)  RequestPort (HITL gate)
                                     |                          |
                                     v                          v
                            IBankApiClient (HTTP) <---- RestWorkflowExecutor
                                     |
                       scope ends --> state written ONCE + response sanitized
```

### Projects (`src/`)

- **Banking.Domain** — Cross-cutting, bank-agnostic pieces only: constants (`ToolNames`, `TraceCategories`, `WorkflowPhases`, `WorkflowStatuses`, `AgentConstants`), `MkCodeDefinition`, `WorkflowMetadata`, `Serialization/JsonPolicy` (the single JSON-formatting source for the whole system — tool results, A2A bridge responses, trace payloads, HTTP bodies — all via `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so Turkish characters and quotes survive unescaped), and `Semantic/` — the single vector-search implementation `SemanticIndex<T>`, its persistence seam `ISemanticIndexStore`/`InMemorySemanticIndexStore`, and its in-process consumer `SemanticWorkflowSearcher`. **Has no project references at all, by design** (see the comment in its `.csproj`). There is no screen-code registry here — mkCode data lives in the bank (see `Banking.Mcp/Semantic/MkCodeCatalog`).
- **Banking.Mcp** — MCP server tools (`AccountsTools`, `CardsTools`, `AnalyzeTools`, `LoansTools`, `PaymentsTools`, `InvestmentsTools`, `CustomerTools`) exposed via `[McpServerToolType]`/`[McpServerTool]`; `ToolRegistry` (auto-scans the assembly at first access); `BankApiToolGateway` (the tools' single exit to the bank); `CustomerContext`; and `Semantic/` — the bank-fed searchers (`MkCodeCatalog`+`SemanticMkCodeSearcher`, `SemanticCampaignSearcher`, `SemanticFinancingSearcher`) plus `CampaignSyncService`. All four bank-fed catalogs share `ISemanticIndexStore` so a repeated refresh with unchanged content skips re-embedding.
- **Banking.Agents** — MAF wiring, one folder per responsibility:
  - `Runtime/` — `AgentRuntime` (orchestrator lifecycle, semantic candidate search, workflow start, previous-turn context), `AgentOptions` (including the per-agent-label `Models` map and `AgentIdleTimeoutMinutes`), `TracedChatClientFactory` (one `ChatClient` per distinct model, keyed by model name), `NavigationTools`, `AgentJsonPolicy` (serializer options for C#-written `AIFunctionFactory` tools — mirrors `Banking.Domain`'s `JsonPolicy.Readable` so MAF's own `\uXXXX`-escaping default doesn't slip back in through this one path).
  - `Sessions/` — **the only place session state is touched.** `SessionContext` (the single door), `ISessionStore`/`InMemorySessionStore` (persistence), `SessionState` (pure data), `AmbientSessionAccessor` + `SessionLease` (ambient scope + per-session lock), `AgentConversationStore`.
  - `Workflows/Engine/` — `WorkflowEngine` (the single engine for every workflow), `WorkflowRegistry` (catalog + engine factory), `WorkflowAgentFactory`, `WorkflowAgentDefinition`, `WorkflowAgentSignals`.
  - `Workflows/Slots/` — `SlotBinder` + `SlotStatus` (pure dictionary ↔ schema comparison).
  - `Workflows/Approval/` — `DeterministicActionWorkflow` (MAF `RequestPort` graph), `ApprovalIntentParser`.
  - `Workflows/Actions/` — `RestWorkflowExecutor` (**the only executor**) and `WorkflowActionDefinition`.
  - `Workflows/Declarative/` — `DeclarativeWorkflowBuilder`, `MicrosoftAgentDeclarativeDefinition`.
  - `Workflows/Contracts/` — `WorkflowStepResult`, `WorkflowOutcome`, `GenericConfirmationView` (namespace `Banking.Agents.Workflows`, no sub-namespace).
  - `Validation/` — mechanism only, no banking rules: `SchemaValidatedSlotWriter`, the `update_slot` choke point.
  - `Specialists/`, `Mcp/` (single `McpToolProvider`), `Prompts/`, `Tracing/`.
- **Banking.BankApi.Client** — `IBankApiClient`/`BankApiClient`, `BankApiOptions`, call traces. **The system's only door to the bank.** Survives the fake bank's removal.
- **Banking.Gateway** — REST-facing layer: `FlowRouter` (routes to active workflow vs. orchestrator), `GatewayService` (opens the session scope + sanitizes), `GatewayResponseMapper` (protocol mapping), `ResponseSanitizer` (output guard).
- **Banking.Host** — ASP.NET Core composition root (`Program.cs`, `Composition/`), MCP/A2A endpoint hosting, static Web UI (`wwwroot/index.html`), on-disk declarative persistence (`declarative-agents/*.yaml`, `specialist-agents/*.yaml`).
- **Banking.BankApi** — **temporary stand-in bank.** Runs as its own process on port 5001. Nothing else references it.

`tools/ApiDump`, `tools/WorkflowTest`, `tools/LoadTest` are standalone diagnostic/manual-test console apps, not part of the runtime.

### Key architectural decisions

0. **One engine for every workflow — no base class, no per-workflow engine code.** `WorkflowEngine` (`Workflows/Engine/WorkflowEngine.cs`) is the *only* workflow runner and is **not generic**. Its single public method `AdvanceWorkflowStepAsync(userMessage, ct)` switches on `SessionContext.Phase` into exactly three private methods: `StartWorkflowAndCollectFirstSlotsAsync` → `CollectSlotsAsync` → `ApplyApprovalDecisionAsync`. Workflows differ only in the `WorkflowActionDefinition` read from YAML; there is nothing to subclass or override. When debugging, ask *which phase?* and go straight to that method. The engine is created per request (`WorkflowRegistry.CreateEngineAsync`) because it binds to that request's `SessionContext`; it holds no state of its own.

1. **YAML is the only way to register a workflow.** There is no `[Workflow]` attribute, no slot `record`, no per-workflow C# executor, no DI line. `DeclarativeWorkflowBuilder` parses Microsoft Agent Framework–style YAML/JSON (`MicrosoftAgentDeclarativeDefinition`) at runtime. `POST /api/workflows/register-declarative` registers it into `WorkflowRegistry`, persists it to `src/Banking.Host/declarative-agents/*.yaml`, and immediately indexes it into `SemanticWorkflowSearcher` — zero cold start, zero rebuild. Files already on disk are reloaded at startup (`StartupLoader`).

2. **Auto-discovered tool registry with a single access path.** `ToolRegistry` (`Banking.Mcp/Registry/ToolRegistry.cs`) scans the assembly for `[McpServerToolType]`/`*Tools`-suffixed classes and groups their methods by `ToolGroup`. **Reach it only via `ToolRegistry.Instance`** — it is deliberately *not* registered in DI, because tool names are read during agent construction, before/independently of the container. `ValidateDiscoveredMcpTools` fail-fasts at startup (`InvalidOperationException`) if the registry and the live MCP server tool list disagree — this is intentional; do not silence it.

3. **Semantic search narrows the orchestrator's choices — it never routes.** `AgentRuntime.SearchCandidateWorkflows` returns the top-3 `SemanticWorkflowSearcher` matches, which `AgentPrompts.BuildCandidateWorkflowsContext` injects as a per-request context message; the orchestrator then decides, and starts a flow only via the `start_workflow` tool. **Do not reintroduce a score threshold that starts a workflow directly.** That bypass existed and was removed because it is unsound by construction, not badly tuned: embedding similarity measures *topic*, never *intent*, so a question and a transaction on the same subject score alike ("Kartımın kullanılabilir limiti nedir?" is a question but scores high against the limit-increase flow). Question-vs-request is a rule in the orchestrator's instructions (`SORU İLE TALEBİ AYIR`) and can only be applied by reading the sentence. What the search buys is scope reduction plus the fact that flow names stay out of the system prompt, so YAML-registered flows are routable the moment they load.

4. **One vector index implementation.** `SemanticIndex<T>` (`Banking.Domain/Semantic/`) is the only place similarity search, thresholding, locking, and zero-downtime `ReplaceAllEntries` live. The four searchers configure it with a key selector, a text selector, and a threshold. **The thresholds differ on purpose** (workflow `0.5`, mkCode `0.70`, campaign `0.70`, financing `0.65`) and each carries a comment saying why — do not unify them. Filtering happens **before** ordering and `Take`, so a below-threshold record can never consume a `topK` slot.

5. **Deterministic bypass while a workflow is active.** `FlowRouter` reads `sessionAccessor.Current.HasActiveWorkflow` — if true, the message goes straight to the workflow engine, never back through the LLM orchestrator. This guarantees a user mid-payment can't be derailed by orchestrator reasoning. The one way back out is the workflow agent's own `handoff_*` tool (see 5b); because that path re-enters `AskOrchestratorAsync` with a workflow still active, `StartWorkflowAsync` refuses to begin a new workflow when `HasActiveWorkflow` is true, and `AskOrchestratorAsync` withholds the candidate list in that case — otherwise an off-topic question could silently discard a half-filled payment.

5b. **The workflow agent's three exits are tools, never phrases.** A slot-filling agent signals the engine only via tool results — `propose_*` (`PROPOSAL_READY:`), `cancel_*` (`CANCELLED`), `handoff_*` (`HANDOFF_TO_ORCHESTRATOR`); the engine reads those from `SessionContext.ConsumeAgentSignal()`, written by the tool itself. Never reintroduce text sniffing of the agent's prose: the model does not copy tool results into its reply, so a phrase search makes the cancel and hand-off paths dead code — and an agent given no exit answers an off-topic message by **inventing** the missing slot value. Correspondingly, `AgentPrompts.BuildSlotFillingRules` carries the "never invent a value" rule for every workflow — keep both halves.

6. **Human-in-the-loop via MAF `RequestPort`.** Any action with financial effect goes through `RequestPort.Create<GenericConfirmationView, bool>()` (`Workflows/Approval/DeterministicActionWorkflow.cs`), which pauses the MAF graph until an explicit user confirmation arrives — the LLM never executes money-moving actions directly. The graph is built and run **inside a single HTTP request** (confirmation is delivered to the port immediately), so no run is left suspended on the server and no sticky sessions are needed. Whether the gate is built at all is decided by `WorkflowActionDefinition.CallsBank` — i.e. by whether the YAML declares an `action.path` — not by workflow name or kind. A workflow that never calls the bank has nothing to approve; routing it to the payment-confirmation screen shows the user an action that will not happen. **The gate must never be skipped for a flow that does have a path.**

7. **Tool grouping limits blast radius and token usage.** Each compiled specialist (`HesapUzmani`, `KrediKartiUzmani`, `AnalizUzmani`) only receives MCP tools from its own `ToolGroup` (`Account`/`Card`/`Analyze`) via `AgentCard.ToolGroup` and `McpToolProvider.GetTracedToolsForGroupAsync`. Declarative specialists may instead list explicit tool names (`AgentCard.ToolNames`); the two are never combined. Deleting the three compiled specialists would remove the system's entire Q&A capability. **Every new `*Tools` class must carry `[ToolGroup(...)]`** — `ToolRegistry.InferGroupFromTypeName` falls back to `Account` for an unrecognised name, which would silently widen `HesapUzmani`'s reach. `Loan`/`Payment`/`Investment`/`Customer` are the groups behind the four YAML specialists (`specialist-agents/kredi|odeme|yatirim|musteri.yaml`), which reach them by explicit tool name.

8. **A2A vs. in-process specialists is a runtime toggle.** `AgentOptions.UseA2AForSpecialists` selects whether the orchestrator talks to specialists over HTTP loopback A2A or in-process. Specialist names, tool groups, bridge tool names and A2A routes all come from `AgentCard` — never repeated in `Program.cs` or in prompts. **Compiled and declarative specialists are built identically** (`SpecialistCatalog.BuildFromCardAsync`); a compiled specialist is just a class exposing a static `AgentCard Card` property. There is no per-specialist `Build` method — the card is the whole contract. Compiled cards are discovered by reflection **once** into a static cache; declarative cards stay in a `ConcurrentDictionary` because they can be added at runtime.

8a. **Specialist agent instances are lazy and idle-evicted, not built at startup.** `SpecialistCatalog.EnsureInitializedAsync` only prepares the in-process MCP connection; the actual `AIAgent` per card is built on first `GetSpecialistAsync` and cached in `IMemoryCache` with a sliding `AgentOptions.AgentIdleTimeout` (default 20 min) — an idle specialist is evicted and rebuilt on the next request, logged via `TraceHub`. This bounds a dictionary that would otherwise only grow (specialists can be registered at runtime). **Eviction never bumps `SpecialistCatalog.Version`** — that would trigger the orchestrator and every workflow agent to rebuild just because one idle specialist was dropped. In in-process mode, the bridge tool wraps a `LazySpecialistAgent` (resolves the target by name on every call) instead of the concrete agent — wrapping the concrete instance would keep it alive via the tool list regardless of cache eviction, silently diverging from A2A mode where the bridge already holds only an `A2AClient`. `WorkflowAgentFactory`'s per-workflow-agent cache follows the same `IMemoryCache` + idle-eviction pattern.

8b. **One session, one door, one read, one write.** All session state lives in `SessionState` and is reached **only** through `SessionContext`; `ISessionStore` is called from exactly one place (`AmbientSessionAccessor`). `OpenSessionScopeAsync` loads state once and takes a per-session lock, `SessionLease.EnterAmbientScope()` makes it ambient, and disposal writes once and releases. Consequences to preserve when editing:
   - `SessionContext` methods are **synchronous** — the data is already in memory, there is no I/O.
   - Never inject `ISessionStore` anywhere else. Multiple reads would return separate copies under Redis and one layer would silently overwrite another's writes.
   - `OpenSessionScopeAsync` (async) and `EnterAmbientScope()` (**sync**) are separate on purpose: `AsyncLocal` mutations inside an `async` method do not propagate back to the caller, so `EnterAmbientScope()` must run in the caller's context. Never make it async.
   - The per-session lock is what makes double-clicking "Approve" safe; combined with `SessionContext.IdempotencyToken` + `CompletedOutcome`, the action runs exactly once.
   - `ISessionStore` is kept as an interface **on purpose** — it is the documented one-line swap point for Redis, which a multi-instance deployment will need. It is the only single-implementation interface left in the codebase; the others were removed because they had no possible second implementation.

8c. **Ending a workflow clears the orchestrator's memory too.** `SessionContext.ResetWorkflow` (called by `CompleteWorkflow`/`CancelWorkflow`) nulls `SerializedOrchestratorSession` alongside the workflow state; the turn context `TopicContextProvider` holds travels inside that serialized history, so there is no second field to clear. During a workflow the router bypasses the orchestrator, so the last thing left in its history is *"I called `start_workflow` and the tool returned a question."* If that is not cleared, the next message after a completed payment is read as an answer to that unanswered question and **the system behaves as though it is still waiting for approval.** The cost — a pronoun referring back across a completed transaction no longer resolves — is the correct trade for a money-moving flow.

9. **Previous-turn context lives in an `AIContextProvider`, not in `AgentRuntime`.** `ContextProviders/TopicContextProvider` (attached via `ChatClientAgentOptions.AIContextProviders` in `AgentRuntime.BuildOrchestrator`) injects the previous turn's assistant reply before the turn and stores the new one after it, so pronoun follow-ups ("ikincisini", "bunlardan bana uygun olanı") resolve. It clears the context when the turn triggered `start_workflow` or `open_screen` — the decision is read from the **tool name constants** (`TopicContextProvider.ContextResettingTools`), never from the reply text, because keyword-matching prose also fires on ordinary user sentences. Keeping both halves inside the provider means the add-before and store-after cannot drift apart. Workflow completion/cancellation is not in that list: the router bypasses the orchestrator during a workflow, so the provider never sees those turns — clearing is handled by `SessionContext.ResetWorkflow` (see 8c).

10. **6-layer output guard (defense in depth).** `ResponseSanitizer` (`Banking.Gateway/ResponseSanitizer.cs`) runs on every response text before it reaches the client via `GatewayService.FinalizeResponse` — the single exit point — stripping/masking: internal tool names, chain-of-thought phrases, internal XML prompt tags, leaked system-prompt fragments, internal endpoints/URLs, and session tokens. It is deterministic regex cleanup, independent of whether the LLM followed instructions — treat it as a hard security boundary. **When you add a new XML tag to a prompt, add it to `XmlTagPattern` and `XmlBlockPattern`**; the lists are enumerated and will not catch a tag you forget.

11. **Everything discoverable is reflection/attribute-driven by design** — when adding a new workflow, specialist, or tool, prefer adding a YAML file or a class + attribute over touching routing code.

12. **Every model turn is measured.** `TracingChatClient` publishes a `"{label} -> Token Maliyeti"` trace line after every completed turn (`girdi`/`çıktı`/`toplam` tokens when the provider returns usage, the model name, and elapsed ms) — the metric the scaling plan (`docs/FINAL-PLAN.md`) optimizes is model turns per message, not per-user latency. A single-question round trip against the orchestrator + one specialist is 4 turns by design (route → delegate → specialist answer → orchestrator relay); don't "fix" that count without re-reading the plan's turn-reduction phases first.

13. **Per-agent model routing is a label→model map, not a code branch.** `AgentOptions.Models["Orkestratör"] = "small-model"` (config or `Agents__Models__<Label>` env var) makes `TracedChatClientFactory.CreateTracedChatClient(label)` resolve a different `ChatClient` for that label while every other label keeps `AgentOptions.Model`; clients are cached per resolved model name so agents sharing a model share a connection. An empty map (the default) is a no-op — this exists so a measured model choice can be applied without a code change, not as a feature to wire up speculatively. Non-ASCII label keys (`Orkestratör`) can't be set via a bash `export` (not a valid POSIX identifier) — use `appsettings.json`, PowerShell (`$env:`), or an ASCII alias if scripting this in a Linux/Docker deployment.

### Adding things (the expected extension points)

- **New workflow:** `POST` a MAF YAML/JSON definition to `/api/workflows/register-declarative`, or drop a `.yaml`/`.json` file into `src/Banking.Host/declarative-agents/`. No C# at all. If it moves money, it must declare `action.path` so the HITL gate is built. Slot names are the request body keys verbatim — they must match the bank contract's field names (`Banking.BankApi/Models/ApiRequests.cs`) or the field arrives as `null`. **A conditionally required slot is declared `required: false`** and its condition goes in `instructions`, stated against an already-filled slot's value ("teslimat kanalı Adres ise adresi sor, Şube ise hiç sorma"); `required` is static and cannot express it, and a condition phrased as a judgement call is never applied. The bank still enforces the pairing and rejects with its own Turkish reason.
- **New MCP tool:** add a `[McpServerTool]` method to a `*Tools` class (or a new class ending in `Tools`), optionally with `[ToolGroup(ToolGroup.X)]`; register the class with `.WithTools<T>()` in `ServiceRegistration`. `ToolRegistry` discovers it; the fail-fast validator catches mismatches at startup.
- **New specialist:** either a YAML file in `src/Banking.Host/specialist-agents/` (or `POST /api/specialists/register-declarative`), or a class exposing a static `AgentCard Card` property (see `Specialists/HesapUzmaniAgent.cs`) — `SpecialistCatalog` finds it via reflection. One file, no other edits. A declarative definition may not reuse a compiled specialist's name, bridge tool name, or A2A route.

### Security-sensitive files

Treat these as the security boundary of the system — be deliberate about any change here:
- `src/Banking.Gateway/ResponseSanitizer.cs` — output guard (see point 10); `GatewayService.FinalizeResponse` is the single exit point every response passes through.
- `src/Banking.Mcp/Registry/ToolRegistry.cs` — `ValidateDiscoveredMcpTools`, invoked from `McpToolProvider` when the MCP connection is established.
- `src/Banking.Agents/Workflows/Approval/*` and `WorkflowEngine.ApplyApprovalDecisionAsync` — HITL approval gate and idempotency for money-moving actions.
- `src/Banking.Agents/Sessions/*` — session isolation. `AmbientSessionAccessor` refuses to nest a *different* session inside an active one; that check is what prevents one user's data reaching another. Note that no session id is ever a tool parameter — `update_slot(key, value)` deliberately has no session argument.
- `src/Banking.Mcp/CustomerContext.cs` — whose data is being read. No silent default.
- `src/Banking.Agents/Validation/SchemaValidatedSlotWriter.cs` — the `update_slot` choke point. Enforces schema types only; banking rules (balance, limit, IBAN) belong to the bank's own endpoints and must not be duplicated here.

## Reference docs in this repo

- **`docs/FINAL-PLAN.md` — the active work plan (Turkish). Read this first when asked to continue
  development.** Ten phases (FAZ 0–9). **Status as of the last dev-branch pass:**
  - Done and verified on `dev`: FAZ 0 (token/turn measurement), FAZ 1 (lazy specialist/workflow-agent
    lifetime + idle eviction), FAZ 2 (mkCode catalog moved to the bank + `ISemanticIndexStore` seam),
    FAZ 3 (per-agent model map + top-10 mkCode candidates so the model picks, not the ranker), FAZ 7
    (Ollama inference settings — see the note below; no code change, per the plan).
  - **Tried and reverted:** FAZ 6.1 (conversation history trimming). Implemented and measured — only
    ~5% input-token reduction on an 8-turn conversation, because the orchestrator's tool results are
    already the specialist's summarized text, not raw bank payloads; removed as not worth the added
    surface (two call-site signature changes, a new config key). If revisiting, read the rejection
    note in `docs/MAF-API-NOTES.md` first — don't re-derive the same measurement.
  - **Deliberately deferred to `main`:** FAZ 4/5/8 (all Redis — session store, distributed lock, tool
    cache) and FAZ 6.2 (context injection, which the plan itself gates on FAZ 5.2). `dev` stays
    Redis-free by design; `ISessionStore`/`InProcessSessionLockProvider` remain the only implementation.
  - `docs/PERFORMANS-VE-OLCEKLEME-PLANI.md` and `docs/UYGULAMA-TALIMATI.md` (the two documents
    FINAL-PLAN superseded) are already gone from the tree — nothing left to archive for that part of
    FAZ 9.3.
  - **Ollama inference note (FAZ 7):** `OLLAMA_CONTEXT_LENGTH` unset lets Ollama allocate the model's
    full advertised context as KV cache — measured on this machine (RTX 4060 8 GB, `gemma4:e4b`) as
    only 34% of the model resident in VRAM (66% offloaded to CPU) at the model's default 128k context,
    vs. 100% resident at a 4k window. `OLLAMA_NUM_PARALLEL` multiplies that KV allocation — a value
    that fits at one context length can OOM at startup with a larger one; balance
    `context_length × num_parallel` against actual GPU memory, don't just copy the plan's example
    values. `ChatOptions.Reasoning.Effort` (`Microsoft.Extensions.AI`) exists and Ollama's
    OpenAI-compatible endpoint accepts `reasoning_effort` without erroring, but a single-sample probe
    showed no reliable monotonic effect on completion length for `gemma4` — don't wire this into
    `TracedChatClientFactory` without a proper multi-run measurement first.
- `README.md` — architecture overview and MAF built-in API usage (Turkish).
- `ANALIZ.md` — class-by-class architecture and end-to-end scenario walkthroughs with Mermaid diagrams (Turkish).
- `docs/PROMPT-YAZIM-REHBERI.md` — how every agent instruction is written (the five-block pattern, shared rule constants, manual test cases) (Turkish). Read this before editing `Prompts/AgentPrompts.cs`.
- **`docs/DOKUMANTASYON-STANDARDI.md` — how `///` XML doc comments and inline `//` comments are written: ten rules (K1–K10), the terminology glossary, and the compiler-assisted verification recipe (Turkish). Read this before adding or editing any comment.** The load-bearing rules: a member's `<summary>` is at most 2 lines and a type's at most 6 (`<para>` is never used); the second sentence exists only when there is a caller-binding constraint; counterfactual narrative is banned (write `verilmez`, not `verilseydi … belirlerdi`); `<b>`/`<i>` are not used; an inline comment is a 2–4 word step label, max three per method. Design rationale, rejected alternatives and measurements do not live in code — they go to `CHANGELOG.md`, this file, or `docs/FINAL-PLAN.md`.
- `docs/MAF-API-NOTES.md` — verified-by-reflection notes on the installed MAF/MCP/A2A package APIs, including old→new renames (e.g. `CreateAIAgent`→`AsAIAgent`, `AgentThread`→`AgentSession`). Consult this before writing MAF code — the framework moves fast and training-data-era API names often don't compile against the installed versions.
- `CHANGELOG.md` — architectural change history with rationale per change (Turkish); useful for understanding *why* a pattern exists.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
