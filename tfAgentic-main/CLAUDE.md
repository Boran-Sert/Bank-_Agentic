# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Turkish-language fintech agentic banking assistant (.NET 9) built on **Microsoft Agent Framework (MAF)**, **Model Context Protocol (MCP)**, and **Agent-to-Agent (A2A)**. Code comments, prompts, trace messages, and commit-adjacent docs (README.md, CHANGELOG.md, ANALIZ.md) are all in Turkish — match that when editing existing files or writing user-facing/prompt strings; C# identifiers mix Turkish domain terms (`HesapUzmani`, `KrediKartiUzmani`) with English framework terms.

## Commands

```powershell
# Run the app (serves API + Web UI on http://localhost:5080)
$env:OPENAI_API_KEY = "sk-..."
dotnet run --project src/Banking.Host

# Build everything
dotnet build BankingAgents.sln

# Build a single project
dotnet build src/Banking.Agents/Banking.Agents.csproj

# Manual deterministic-workflow scenario runner (no LLM calls; simulates HITL approve/reject)
dotnet run --project tools/WorkflowTest

# Dump reflected API surface of installed MAF/MCP/A2A assemblies to maf_api.txt
dotnet run --project tools/ApiDump
```

There is no automated test project (no xUnit/NUnit project in the solution) — `tools/WorkflowTest` is a manual console scenario runner used to sanity-check the deterministic HITL workflow end-to-end, and `test.http` has example HTTP requests against the running host. When validating behavior, prefer running the host and hitting `/api/chat`, `/api/diag`, or replaying `test.http` requests, or extending `tools/WorkflowTest`.

The API key resolves in this order: User Secrets / `appsettings.json` (`Agents:ApiKey`) → `OPENAI_API_KEY` env var. `src/Banking.Host` has a `UserSecretsId` configured.

## High-level architecture

Request flow:

```
Browser (wwwroot/index.html) --/api/chat--> GatewayService
                                                  |
                       ISessionAccessor.RunInSessionScopeAsync  <-- state loaded ONCE
                                                  |
                                             FlowRouter (deterministic router)
                                     +------------+-------------+
                            (no active workflow)        (active workflow)
                                     |                          |
                                     v                          v
                            Orchestrator Agent          WorkflowDispatcher
                          (AgentRuntime, A2A)                   |
                                     |                          v
                       +-------------+-------------+   WorkflowEngine<TSlots>
                       v             v             v     (phase state machine)
                 HesapUzmani  KrediKartiUzmani AnalizUzmani     |
                       |             |             |     Start / SlotFilling / Approval
                       +------------>+<------------+            |
                                     v                          v
                              Banking.Mcp (IToolRegistry)  RequestPort (HITL gate)
                                     |                          |
                                     v                          v
                              Banking.Domain            IDeterministicActionExecutor
                                                  |
                       scope ends --> state written ONCE + response sanitized
```

### Projects (`src/`)

- **Banking.Domain** — Models, in-memory bank data store (`InMemoryBankStore`), account/card/collection/transfer services, `[Workflow]`-tagged slot records, and ONNX-based semantic search engines (`SemanticMkCodeSearcher`, `SemanticCampaignSearcher`, `SemanticFinancingSearcher`, `SemanticWorkflowSearcher`) using `SmartComponents.LocalEmbeddings`. No dependency on any other project in the solution.
- **Banking.Mcp** — MCP server tools (`AccountsTools`, `CardsTools`, `AnalyzeTools`) exposed via `[McpServerToolType]`/`[McpServerTool]`, plus the `ToolRegistry` (singleton, `ToolRegistry.Instance`) that auto-scans the assembly for tools at first access.
- **Banking.Agents** — MAF wiring, organised into one folder per responsibility:
  - `Runtime/` — `AgentRuntime` (orchestrator lifecycle + semantic routing + diagnostics), `AgentOptions`, `ChatClientFactory`, `DeferredSpecialistAgent`, `NavigationTools`.
  - `Sessions/` — **the only place session state is touched.** `SessionContext` (the single door), `ISessionStore`/`InMemorySessionStore` (persistence), `SessionState` (pure data), `ISessionAccessor`/`AmbientSessionAccessor` (ambient scope + per-session lock).
  - `Workflows/Engine/` — `WorkflowEngine<TSlots>` (the single engine for every workflow), `WorkflowDispatcher`, `WorkflowRegistry`, `WorkflowAgentFactory`, `WorkflowAgentDefinition`, `WorkflowAgentSignals`.
  - `Workflows/Slots/` — `SlotBinder` (pure dictionary ↔ record binding), `SlotValueConverter`, `SlotConstructionResult`.
  - `Workflows/Approval/` — `DeterministicActionWorkflow` (MAF `RequestPort` graph), `ApprovalIntentParser`.
  - `Workflows/Actions/` — `PaymentActionExecutor`, `TransferActionExecutor`, `DeclarativeActionExecutor`. Each one carries *all* of a workflow's bank-facing surface: pre-validation, confirmation view, and the action itself.
  - `Workflows/Declarative/` — `DeclarativeWorkflowBuilder`, `MicrosoftAgentDeclarativeDefinition`.
  - `Workflows/Contracts/` — public surface used by every layer: `IWorkflowEngine`, `IDeterministicActionExecutor<TSlots>`, `WorkflowStepResult`, `WorkflowOutcome`, `GenericConfirmationView` (namespace `Banking.Agents.Workflows`, no sub-namespace).
  - `Validation/` — mechanism only, no banking rules: `BankValidatedSlotWriter` (the `update_slot` choke point) and `PartiallyFilledSlotReader` (keeps "absent" distinct from "zero").
  - `Specialists/`, `Mcp/` (single `McpToolProvider`), `Prompts/`, `ContextProviders/`, `Tracing/`.
- **Banking.Gateway** — REST-facing layer: `FlowRouter` (routes to active workflow vs. orchestrator), `GatewayService` (opens the session scope + sanitizes), `GatewayResponseMapper` (protocol mapping), `ResponseSanitizer` (output guard).
- **Banking.Host** — ASP.NET Core composition root (`Program.cs`), MCP/A2A endpoint hosting, static Web UI (`wwwroot/index.html`), and on-disk declarative agent persistence (`declarative-agents/*.yaml`).

`tools/ApiDump` and `tools/WorkflowTest` are standalone diagnostic/manual-test console apps, not part of the runtime.

### Key architectural decisions (all deliberately dynamic, not hardcoded)

0. **One engine for every workflow — no base class, no per-workflow engine code.** `WorkflowEngine<TSlots>` (`Workflows/Engine/WorkflowEngine.cs`) is the *only* workflow runner. Its single public method `AdvanceAsync(userMessage, ct)` switches on `SessionContext.Phase` into exactly three private methods: `StartAsync` → `CollectSlotsAsync` → `ApplyApprovalDecisionAsync`. Workflows differ only in `TSlots` and their `IDeterministicActionExecutor<TSlots>`; there is nothing to subclass or override. When debugging, ask *which phase?* and go straight to that method.
1. **Reflection-driven generic slot filling.** Any `record` decorated with `[Workflow("name")]` (see `Banking.Domain.Attributes.WorkflowAttribute`, e.g. `PaymentSlots`, `TransferSlots`) is automatically driven through `WorkflowEngine<TSlots>` — no per-workflow C# workflow code needed. `WorkflowRegistry.AutoDiscoverWorkflows(slotAssembly, executorAssembly)` scans **both** assemblies at startup (slot records live in Banking.Domain, executors in Banking.Agents — passing only one registers nothing) and wires them together. Dictionary ↔ record binding is pure and lives in `SlotBinder`.
2. **Declarative (no-recompile) agents.** Beyond the reflection-based C# workflows, `DeclarativeWorkflowBuilder` parses Microsoft Agent Framework–style YAML/JSON agent definitions (`MicrosoftAgentDeclarativeDefinition`) at runtime. `POST /api/workflows/register-declarative` accepts YAML or JSON, registers it into `WorkflowRegistry`, persists it to `src/Banking.Host/declarative-agents/*.yaml`, and immediately indexes it into the semantic RAG router (`SemanticWorkflowSearcher`) — zero cold start, zero rebuild. Files already on disk in `declarative-agents/` are reloaded automatically on startup (`Program.cs`).
   **A declarative definition may not reuse the name of a compiled workflow** — `WorkflowRegistry` throws `InvalidOperationException` (HTTP 400 / startup warning). Otherwise a text file could silently replace a money-moving executor with a "request recorded" stub, and the user would be told the payment succeeded when nothing happened. Corollary: `DeclarativeActionExecutor` **never moves money** and issues no receipt, so any flow with financial effect (`card-payment`, `money-transfer`) must stay a compiled executor.
3. **Auto-discovered tool registry.** `ToolRegistry` (`Banking.Mcp/Registry/ToolRegistry.cs`) scans the assembly for `[McpServerToolType]`/`*Tools`-suffixed classes and groups their methods by `ToolGroup` (via `[ToolGroup]` attribute or name-based inference). `ValidateDiscoveredMcpTools` fail-fasts at startup (`InvalidOperationException`) if the registry and the live MCP server tool list disagree — this is intentional; do not silence it.
4. **Semantic (vector) routing before the LLM.** `AgentRuntime.AskOrchestratorAsync` runs the user message through `SemanticWorkflowSearcher` first; a match bypasses the LLM orchestrator entirely and starts the workflow directly (determinism + cost savings). Below that, top matches are injected as dynamic prompt context instead. The bypass requires **two** conditions, not one: score ≥ `SemanticWorkflowMatchThreshold` (0.65) **and** a lead of ≥ `SemanticWorkflowMatchMargin` (0.05) over the runner-up. Embedding similarity measures *topic*, not *intent* — "Kartlarımı göster" scores 0.751 against a limit-increase workflow — so clustered scores mean the search did not discriminate and there is no deterministic answer to bypass with. Do not drop the margin check back to a bare threshold.
5. **Deterministic bypass while a workflow is active.** `FlowRouter` checks `WorkflowDispatcher.HasActiveWorkflow` — if true, messages go straight to the active workflow engine, never back through the LLM orchestrator. This guarantees a user mid-payment can't be derailed by orchestrator reasoning. The one way back out is the workflow agent's own `handoff_*` tool (see 5b); because that path re-enters `AskOrchestratorAsync` with a workflow still active, both `AskOrchestratorAsync` (semantic auto-start) and `StartWorkflowAsync` (the `start_workflow` tool) refuse to begin a new workflow when `HasActiveWorkflow` is true — otherwise an off-topic question could silently discard a half-filled payment.
5b. **The workflow agent's three exits are tools, never phrases.** A slot-filling agent signals the engine only via tool results — `propose_*` (`PROPOSAL_READY:`), `cancel_*` (`CANCELLED`), `handoff_*` (`HANDOFF_TO_ORCHESTRATOR`); `WorkflowEngine.InterpretAgentOutputAsync` matches those constants and nothing else. Never reintroduce text sniffing of the agent's prose (the old `OffTopicMarkers` array searched for "yardımcı olamıyorum" — a phrase no prompt ever asked the agent to emit, so the hand-off path was dead code and the agent, given no exit, answered an off-topic message by **inventing** the missing slot value and pushing a payment the user never assembled to the HITL screen). Correspondingly, `AgentPrompts.BuildSlotFillingRules` carries the "never invent a value" rule for every workflow, compiled and declarative alike — keep both halves.
6. **Human-in-the-loop via MAF `RequestPort`.** Any action with financial effect goes through `RequestPort.Create<GenericConfirmationView, bool>()` (`Workflows/Approval/DeterministicActionWorkflow.cs`), which pauses the MAF graph until an explicit user confirmation arrives from the UI — the LLM never executes money-moving actions directly. The graph is built and run **inside a single HTTP request** (confirmation is delivered to the port immediately), so no `StreamingRun` is ever left suspended on the server and no sticky sessions are needed.
7. **Tool grouping limits blast radius and token usage.** Each specialist agent (`HesapUzmani`, `KrediKartiUzmani`, `AnalizUzmani`) only receives MCP tools from its own `ToolGroup` (`Account`/`Card`/`Analyze`), via `AgentCard.ToolGroup` and `McpToolProvider.GetTracedToolsForGroupAsync(...)`. These three specialists are compiled C#, **not** declarative YAML — the declarative schema has no notion of tool groups or A2A routes, and deleting them would remove the system's entire Q&A capability.
8. **A2A vs. in-process specialists is a runtime toggle.** `AgentOptions.UseA2AForSpecialists` selects whether the orchestrator talks to specialists over HTTP loopback A2A or in-process; on A2A connection failure it automatically falls back to in-process (see `SpecialistCatalog.BuildBridgeTools`). Specialist names, tool groups, bridge tool names and A2A routes all come from `AgentCard` — they are never repeated in `Program.cs` or in prompts.
8b. **One session, one door, one read, one write.** All session state lives in `SessionState` and is reached **only** through `SessionContext`; `ISessionStore` is called from exactly one place (`AmbientSessionAccessor`). `ISessionAccessor.RunInSessionScopeAsync` loads state once at the start of a request, takes a per-session lock, runs the work, then writes once and releases. Consequences to preserve when editing:
   - `SessionContext` methods are **synchronous** — the data is already in memory, there is no I/O.
   - Never inject `ISessionStore` anywhere else. Multiple reads would return separate copies under Redis and one layer would silently overwrite another's writes.
   - The scope API is delegate-based on purpose: `AsyncLocal` mutations inside an `async` method do **not** propagate back to the caller, so a `BeginScope()`-style API cannot work here.
   - The per-session lock is what makes double-clicking "Approve" safe; combined with `SessionContext.IdempotencyToken` + `CompletedOutcome`, the action runs exactly once.
9. **6-layer output guard (defense in depth, layer 3).** `ResponseSanitizer` (`Banking.Gateway/ResponseSanitizer.cs`) runs on every response text before it reaches the client, stripping/masking: internal tool names, chain-of-thought/planning phrases, internal XML prompt tags, leaked system-prompt fragments, internal endpoints/URLs, and session tokens. It is deterministic regex-based cleanup, independent of whether the LLM followed instructions — treat it as a hard security boundary, not an optional nicety, when touching response paths.
10. **Everything discoverable is reflection/attribute-driven by design**, per the project's stated philosophy of avoiding hardcoded/static structures — when adding a new workflow, specialist, or tool, prefer adding a new class/record + attribute over touching routing code, consistent with the existing pattern.

### Adding a new workflow (the expected extension point)

- **Programmatic (compiled) workflow:** define a `record` with `[Workflow("name")]` + `[Description(...)]` (see `Banking.Domain/Records/PaymentSlots.cs` / `TransferSlots.cs`), implement `IDeterministicActionExecutor<TSlots>` in `Workflows/Actions/` (all three members: `ValidateFilledSlotsAsync` delegating to the bank's dry-run service, `BuildConfirmationView`, `ExecuteAsync`), register it in DI in `Program.cs` with one line. `WorkflowRegistry.AutoDiscoverWorkflows` picks it up automatically at startup — **no engine, routing, session, or prompt code changes**. There is deliberately **one** per-workflow abstraction, not a separate validator interface: two parallel abstractions meant one could change without the other.
- **Declarative (no-recompile) workflow:** `POST` a MAF YAML/JSON definition to `/api/workflows/register-declarative`, or drop a `.yaml`/`.json` file into `src/Banking.Host/declarative-agents/`. The name must not collide with a compiled workflow.

### Adding a new MCP tool / specialist

- New MCP tool: add a `[McpServerTool]` method to a `*Tools` class (or a new one ending in `Tools`), optionally with `[ToolGroup(ToolGroup.X)]`; register the class with `.WithTools<T>()` in `Program.cs`. `ToolRegistry` discovers it automatically; the fail-fast validator will catch registration mismatches at startup.
- New specialist agent: add a class exposing a static `AgentCard Card` property and a static `Build(IChatClient, IList<AITool>)` method (see `Specialists/HesapUzmaniAgent.cs` etc.) — `SpecialistCatalog` finds it via reflection and `Program.cs` registers its A2A endpoint from the same card. One file, no other edits.

### Security-sensitive files

Treat these as the security boundary of the system — be deliberate about any change here:
- `src/Banking.Gateway/ResponseSanitizer.cs` — output guard (see point 9 above); `GatewayService.Finalize` is the single exit point every response passes through.
- `src/Banking.Mcp/Registry/ToolRegistry.cs` — `ValidateDiscoveredMcpTools`, invoked from `McpToolProvider` when the MCP connection is established.
- `src/Banking.Agents/Workflows/Approval/*` and `WorkflowEngine.ApplyApprovalDecisionAsync` — HITL approval gate and idempotency for money-moving actions.
- `src/Banking.Agents/Sessions/*` — session isolation. `AmbientSessionAccessor` refuses to nest a *different* session inside an active one; that check is what prevents one user's data reaching another.
- `src/Banking.Agents/Validation/PartiallyFilledSlotReader.cs` — must keep "value absent" and "value is zero" distinct; conflating them makes the bank reject a payment before the user has even given the amount.

## Reference docs in this repo

- `README.md` — architecture overview and MAF built-in API usage (Turkish).
- `ANALIZ.md` — full class-by-class architecture and end-to-end scenario walkthroughs with Mermaid diagrams (Turkish).
- `docs/MAF-API-NOTES.md` — verified-by-reflection notes on the installed MAF/MCP/A2A package APIs, including old→new renames (e.g. `CreateAIAgent`→`AsAIAgent`, `AgentThread`→`AgentSession`). Consult this before writing MAF code — the framework moves fast and training-data-era API names often don't compile against the installed versions.
- `CHANGELOG.md` — architectural change history with rationale per change (Turkish); useful for understanding *why* a pattern exists.
