# Graph Report - C:\Users\boran\Documents\Projeler\TKFB_Agentic\tfAgentic-main  (2026-08-13)

## Corpus Check
- 179 files · ~104,664 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1636 nodes · 3265 edges · 106 communities (84 shown, 22 thin omitted)
- Extraction: 96% EXTRACTED · 4% INFERRED · 0% AMBIGUOUS · INFERRED: 129 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- Workflow Action Execution & Idempotency
- LoadTest Journey Results
- Banking.Host Wiring & Trace Channel
- Solution & Package Manifest
- Bank API Client Tracing
- Statement Endpoints & Parsing
- Lazy Specialist Agent Proxy
- MCP Tool Provider & Discovery
- Catalog Endpoints (Campaigns/Financing/MkCode)
- Documentation Rules & Acceptance Test
- AnalyzeTools Statement Analysis
- Core Security & Routing Concepts
- Bank API Request DTOs
- Slot Schema Validation
- Session Scope & Locking
- Compiled Specialist Agents
- Investment & FX Endpoints
- MCP Namespaces & Finans Specialist
- Gateway Routing & Response Mapping
- LoadTest HTTP API Client
- Specialist Catalog Lifecycle
- Loan Endpoints & Installments
- Customer Application Service
- LoadTest Report Writer
- LoadTest Bank Snapshot & Validation
- Tracing Chat Client
- Semantic Vector Index Core
- Application Endpoints & Bank Results
- Bill Payment Endpoints
- Standing Order Endpoints
- Declarative Workflow Concepts & Gate
- Orchestrator Runtime
- Slot Binding
- SessionContext — Single Door
- InMemoryBankStore Seeding
- ResponseSanitizer Output Guard
- BankApi Endpoint Namespaces
- Semantic Index Persistence
- Customer Endpoints & Limits
- Scaling Plan Phases (FAZ 0–3)
- API Traffic Logging Middleware
- Account Endpoints & Service
- BankApi Launch Settings
- Host Launch Settings
- Agent Options & Model Routing
- Semantic Financing Search
- Beneficiary Endpoints
- Money Transfer Service
- Prompt Rule Constants
- Card Debt Collection Service
- LoadTest Options & Mix Parsing
- Topic Context Provider
- HITL Approval Test Scenarios
- LoadTest Runner & Leak Scanner
- Bank Domain Models
- Insurance Endpoints
- MkCode Catalog
- LoadTest Metrics Collector
- JSON Policy & Trace Unwrap
- Semantic MkCode Search
- Card Service
- Turkish IBAN Validation
- Card Operations Service
- In-Memory Session Store
- Semantic Workflow Search
- Agent Conversation Store
- CardsTools MCP
- LoadTest Scenario Catalog
- Workflow Registry
- Navigation Tools (Screen Open/Search)
- Declarative Definition Schema
- AccountsTools MCP
- LoadTest Bank Health Probe
- TracingAIFunction Decorator
- Semantic Index Threshold Design
- MAF Action Executor Step
- Bank API Client DI Setup
- Candidate Workflow Search
- ISessionStore Contract
- Session Isolation & Double-Approve Test
- AgentPrompts Instruction Builder
- Agent Constants
- BankClock
- Session Store Options
- ToolNames Constants
- TraceCategories Constants
- WorkflowPhases Constants
- WorkflowStatuses Constants
- LoadTest Project File
- K10 Terminology Glossary
- K1 Line Budget Rule
- K2 First-Sentence Contract
- K3 Second-Sentence Constraint
- K5 No Decoration Rule
- K6 Obvious-Member Rule
- K7 Inline Comment Rule
- K8 Single-Owner Rule
- Cancel Scenario
- Declarative Registration Scenario
- Handoff Scenario
- Multiturn Scenario
- QA Scenario
- Transfer Scenario
- AgentSession Rename Note
- AsAIAgent Rename Note

## God Nodes (most connected - your core abstractions)
1. `InMemoryBankStore` - 34 edges
2. `Banking.Domain.Constants` - 32 edges
3. `Banking.Domain.Services` - 31 edges
4. `JourneyRunner` - 31 edges
5. `Banking.Agents.Tracing` - 26 edges
6. `SessionContext` - 23 edges
7. `SpecialistCatalog` - 21 edges
8. `Banking.Domain.Models` - 19 edges
9. `Banking.Domain.Data` - 18 edges
10. `CardTransactionItem` - 18 edges

## Surprising Connections (you probably didn't know these)
- `%100 Determinizm — Orkestratör Baypas` --semantically_similar_to--> `FlowRouter — Deterministik Orkestratör Baypası`  [INFERRED] [semantically similar]
  README.md → ANALIZ.md
- `FAZ 9 — Dokümantasyon Üslubu` --semantically_similar_to--> `Tak-Çalıştır Kabul Testi`  [INFERRED] [semantically similar]
  docs/FINAL-PLAN.md → ANALIZ.md
- `Neden Dinamik Bir Yapı Kullanıldı` --semantically_similar_to--> `YAML — Tek İş Akışı Kayıt Yolu`  [INFERRED] [semantically similar]
  README.md → ANALIZ.md
- `Sözleşme: Kapısız Akış` --conceptually_related_to--> `CallsBank — Onay Kapısının Kurulma Koşulu`  [INFERRED]
  tools/LoadTest/README.md → ANALIZ.md
- `Kredi Kartı Başvurusu Akışı (kredi-karti-basvuru)` --conceptually_related_to--> `CallsBank — Onay Kapısının Kurulma Koşulu`  [INFERRED]
  src/Banking.Host/declarative-agents/kredi-karti-basvuru.yaml → ANALIZ.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **K1–K10 XML Dokümantasyon Standardı** — dstd_k1_satir_butcesi, dstd_k2_ilk_cumle_sozlesmedir, dstd_k3_ikinci_cumle_kisit, dstd_k4_karsi_olgusal_anlati_yok, dstd_k5_susleme_yok, dstd_k6_asikar_uye_belgelenmez, dstd_k7_satir_ici_yorum_adim_etiketi, dstd_k8_kisitin_tek_sahibi, dstd_k9_gerekcenin_yeri, dstd_k10_terim_sozlugu [EXTRACTED 1.00]
- **AgentPrompts Ortak Kural Sabitleri** — prompt_nostatuschatter, prompt_nointernaldisclosure, prompt_nofabrication, prompt_instructionprecedence, prompt_internalguardrail, prompt_internalidagentchannel, prompt_recordmatchingduty, prompt_slotturndirective, prompt_presentationcontract [EXTRACTED 1.00]
- **LoadTest Sözleşme Denetimleri** — loadtest_para_degismezi, loadtest_onay_kapisi_sozlesmesi, loadtest_cift_onay_sozlesmesi, loadtest_oturum_izolasyonu_sozlesmesi, loadtest_cikti_guvenlik_bariyeri_sozlesmesi, loadtest_kapisiz_akis_sozlesmesi [EXTRACTED 1.00]

## Communities (106 total, 22 thin omitted)

### Community 0 - "Workflow Action Execution & Idempotency"
Cohesion: 0.07
Nodes (30): Idempotency Anahtarı — Çift Tıklama Koruması, CultureInfo, GeneratedRegex, RestWorkflowExecutor, CancellationToken, GenericConfirmationView, IReadOnlyDictionary, JsonElement (+22 more)

### Community 1 - "LoadTest Journey Results"
Cohesion: 0.14
Nodes (20): ConcurrentBag, Journey, ChatResponse, Func, IReadOnlyCollection, JourneyStatus, MoneyEffect, MoneyLedger (+12 more)

### Community 2 - "Banking.Host Wiring & Trace Channel"
Cohesion: 0.06
Nodes (28): ChannelReader, Content, Banking.Host, FilePath, Guid, id, reader, DeclarativeSpecialistBuilder (+20 more)

### Community 3 - "Solution & Package Manifest"
Cohesion: 0.05
Nodes (38): Microsoft.Agents.AI.A2A (1.11.1-preview.260625.1), Microsoft.Agents.AI.Hosting (1.11.1-preview.260625.1), Microsoft.Agents.AI.OpenAI (1.11.1), Microsoft.Agents.AI.Workflows (1.11.1), Microsoft.Extensions.AI (10.8.3), Microsoft.Extensions.AI.Abstractions (10.8.3), Microsoft.Extensions.AI.OpenAI (10.7.0), Microsoft.Extensions.Caching.Memory (9.0.0) (+30 more)

### Community 4 - "Bank API Client Tracing"
Cohesion: 0.08
Nodes (21): Action, TraceHubBankApiObserver, IReadOnlyList, BankApiErrorTrace, BankApiField, BankApiPayloadReader, BankApiRequestTrace, BankApiResponseTrace (+13 more)

### Community 5 - "Statement Endpoints & Parsing"
Cohesion: 0.13
Nodes (19): StatementEndpoints, DateTime, IReadOnlyList, string, WebApplication, StatementRequest, AnalyzeService, BonusAnalysisResult (+11 more)

### Community 6 - "Lazy Specialist Agent Proxy"
Cohesion: 0.11
Nodes (28): AIAgent, LazySpecialistAgent, AgentResponse, AgentResponseUpdate, AgentRunOptions, AgentSession, CancellationToken, ChatMessage (+20 more)

### Community 7 - "MCP Tool Provider & Discovery"
Cohesion: 0.09
Nodes (21): Assembly, Attribute, IList, McpClient, McpClientTool, McpToolProvider, AITool, CancellationToken (+13 more)

### Community 8 - "Catalog Endpoints (Campaigns/Financing/MkCode)"
Cohesion: 0.09
Nodes (18): CatalogEndpoints, WebApplication, Campaign, FinancingOption, MkCodeItem, CampaignCatalogService, ICampaignCatalogService, IReadOnlyList (+10 more)

### Community 9 - "Documentation Rules & Acceptance Test"
Cohesion: 0.07
Nodes (27): Tak-Çalıştır Kabul Testi, Body, K4 — Karşı-Olgusal Anlatı Yok, K9 — Gerekçenin Yeri, Exception, FAZ 9 — Dokümantasyon Üslubu, IdempotencyKey, Method (+19 more)

### Community 10 - "AnalyzeTools Statement Analysis"
Cohesion: 0.15
Nodes (17): AnalyzeTools, CancellationToken, Description, IEnumerable, JsonElement, McpServerTool, Task, CampaignSummary (+9 more)

### Community 11 - "Core Security & Routing Concepts"
Cohesion: 0.14
Nodes (15): FlowRouter — Deterministik Orkestratör Baypası, ResponseSanitizer — 6 Katmanlı Çıktı Bariyeri, SchemaValidatedSlotWriter — Şema Kapısı, Banking.Gateway.Models, Banking.Agents.Tracing, Banking.Domain.Constants, Banking.Agents.Sessions, Banking.Agents.Runtime (+7 more)

### Community 12 - "Bank API Request DTOs"
Cohesion: 0.06
Nodes (31): AccountRequest, AccountTransactionsRequest, ApplicationRequest, BeneficiaryCreateRequest, BeneficiaryRemoveRequest, BillPaymentRequest, BillRequest, BillsRequest (+23 more)

### Community 13 - "Slot Schema Validation"
Cohesion: 0.11
Nodes (14): AIJsonSchemaCreateOptions, JsonValueKind, SchemaValidatedSlotWriter, WorkflowAgentDefinition, CachedWorkflowAgent, WorkflowAgentFactory, AIAgent, AITool (+6 more)

### Community 14 - "Session Scope & Locking"
Cohesion: 0.10
Nodes (19): AsyncLocal, DateTimeOffset, IAsyncDisposable, AmbientSessionAccessor, SessionLock, CancellationToken, Dictionary, Lock (+11 more)

### Community 15 - "Compiled Specialist Agents"
Cohesion: 0.15
Nodes (16): AgentCard, AnalizUzmaniAgent, HesapUzmaniAgent, KartUzmaniAgent, SpecialistCatalog, AIAgent, AITool, bool (+8 more)

### Community 16 - "Investment & FX Endpoints"
Cohesion: 0.14
Nodes (11): InvestmentEndpoints, WebApplication, ExchangeRate, FxTradeReceipt, InvestmentPosition, PortfolioSummary, RegisteredDevice, IInvestmentService (+3 more)

### Community 17 - "MCP Namespaces & Finans Specialist"
Cohesion: 0.10
Nodes (17): Banking.BankApi.Client, Banking.Domain.Serialization, Banking.Mcp.Semantic, Banking.Mcp, Banking.Mcp.Constants, Finansal Uzman (Finans, deklaratif), Kampanya Uzmanı (deklaratif), AccountToolNames (+9 more)

### Community 18 - "Gateway Routing & Response Mapping"
Cohesion: 0.11
Nodes (13): FlowExecutionResult, FlowRouter, CancellationToken, Task, GatewayResponseMapper, GatewayService, CancellationToken, Task (+5 more)

### Community 19 - "LoadTest HTTP API Client"
Cohesion: 0.17
Nodes (13): Error, Events, HttpContent, StringContent, CancellationToken, Func, HttpMethod, JsonElement (+5 more)

### Community 20 - "Specialist Catalog Lifecycle"
Cohesion: 0.14
Nodes (13): SpecialistCatalog — Tembel Kurulum + Boşta Tahliye, Banking.Agents.Specialists.Declarative, Banking.Agents.Prompts, Banking.Host.Composition, Banking.Mcp.Registry, Banking.Agents.Specialists, Banking.Agents.Workflows.Declarative, Banking.Agents.Workflows.Engine (+5 more)

### Community 21 - "Loan Endpoints & Installments"
Cohesion: 0.16
Nodes (9): LoanEndpoints, WebApplication, Loan, LoanInstallment, LoanPaymentReceipt, ILoanService, LoanService, DateOnly (+1 more)

### Community 22 - "Customer Application Service"
Cohesion: 0.19
Nodes (8): Customer, CustomerApplication, ApplicationService, IApplicationService, int, IReadOnlyDictionary, IReadOnlyList, string

### Community 23 - "LoadTest Report Writer"
Cohesion: 0.28
Nodes (5): StringBuilder, IEnumerable, IReadOnlyList, ReportWriter, RunSummary

### Community 24 - "LoadTest Bank Snapshot & Validation"
Cohesion: 0.20
Nodes (9): decimal, IReadOnlyList, List, BankSnapshot, IReadOnlyList, List, InvariantCheck, Invariants (+1 more)

### Community 25 - "Tracing Chat Client"
Cohesion: 0.19
Nodes (13): ChatOptions, ChatResponseUpdate, DelegatingChatClient, TracingChatClient, CancellationToken, ChatMessage, ChatResponse, IAsyncEnumerable (+5 more)

### Community 26 - "Semantic Vector Index Core"
Cohesion: 0.16
Nodes (14): EmbeddingF32, Item, SemanticIndex, CancellationToken, Func, IEnumerable, IReadOnlyList, Key (+6 more)

### Community 27 - "Application Endpoints & Bank Results"
Cohesion: 0.16
Nodes (9): IResult, ApplicationEndpoints, WebApplication, BankResults, Func, CardEndpoints, WebApplication, MoneyEndpoints (+1 more)

### Community 28 - "Bill Payment Endpoints"
Cohesion: 0.19
Nodes (7): BillEndpoints, WebApplication, Bill, BillPaymentReceipt, BillService, IBillService, IReadOnlyList

### Community 29 - "Standing Order Endpoints"
Cohesion: 0.18
Nodes (7): StandingOrderEndpoints, WebApplication, StandingOrder, IStandingOrderService, StandingOrderService, DateOnly, IReadOnlyList

### Community 30 - "Declarative Workflow Concepts & Gate"
Cohesion: 0.15
Nodes (14): CallsBank — Onay Kapısının Kurulma Koşulu, YAML — Tek İş Akışı Kayıt Yolu, RestWorkflowExecutor — 404 Yol Bulunamadı Tanısı, ToolRegistry — Otomatik Keşif ve Fail-Fast Doğrulama, Banking.Agents.Workflows.Actions, Sözleşme: Kapısız Akış, Sözleşme: Para Değişmezi, Senaryo: nonbank — Bankaya İstek Göndermeyen Akış (+6 more)

### Community 31 - "Orchestrator Runtime"
Cohesion: 0.21
Nodes (10): ChatClientAgent, AgentRuntime, OrchestratorReply, AIAgent, AITool, CancellationToken, int, IReadOnlyList (+2 more)

### Community 32 - "Slot Binding"
Cohesion: 0.17
Nodes (8): Banking.Agents.Workflows.Slots, IReadOnlyDictionary, DeclarativePropertyConfig, SlotBinder, SlotStatus, Dictionary, HashSet, IReadOnlyDictionary

### Community 33 - "SessionContext — Single Door"
Cohesion: 0.16
Nodes (7): SessionContext, Dictionary, JsonElement, TraceHub, ConcurrentDictionary, ILogger, JsonSerializerOptions

### Community 34 - "InMemoryBankStore Seeding"
Cohesion: 0.11
Nodes (4): InMemoryBankStore, ConcurrentDictionary, List, string

### Community 35 - "ResponseSanitizer Output Guard"
Cohesion: 0.24
Nodes (5): ResponseSanitizer, IEnumerable, ILogger, Regex, string

### Community 36 - "BankApi Endpoint Namespaces"
Cohesion: 0.30
Nodes (3): Banking.BankApi.Contracts, Banking.BankApi.Endpoints, Banking.Domain.Services

### Community 37 - "Semantic Index Persistence"
Cohesion: 0.20
Nodes (11): EmbeddingF32 — Serileştirme Bulgusu, InMemorySemanticIndexStore, CancellationToken, ConcurrentDictionary, IReadOnlyList, ValueTask, ISemanticIndexStore, StoredVector (+3 more)

### Community 38 - "Customer Endpoints & Limits"
Cohesion: 0.17
Nodes (6): CustomerEndpoints, WebApplication, CustomerLimits, CustomerSecurityProfile, CustomerService, ICustomerService

### Community 39 - "Scaling Plan Phases (FAZ 0–3)"
Cohesion: 0.12
Nodes (13): A2A vs. Süreç İçi — Çalışma Zamanı Anahtarı, TopicContextProvider — Önceki Tur Bağlamı, Banking.Agents.ContextProviders, FAZ 0 — Model Turunun Maliyetini Ölç, FAZ 2 — Katalogları Bankaya Taşı, İndeksi Bir Kez Kur, FAZ 3 — Ucuz Tur Azaltma, FAZ 4 — Redis: Oturum Deposu ve Dağıtık Kilit, FAZ 5 — Redis'e Dayanan Önbellekler (+5 more)

### Community 40 - "API Traffic Logging Middleware"
Cohesion: 0.18
Nodes (9): Banking.BankApi.Diagnostics, HttpContext, HttpRequest, IApplicationBuilder, ApiTrafficLoggingExtensions, ApiTrafficLoggingMiddleware, int, Task (+1 more)

### Community 41 - "Account Endpoints & Service"
Cohesion: 0.22
Nodes (8): AccountEndpoints, WebApplication, Account, Transaction, AccountService, IAccountService, DateOnly, IReadOnlyList

### Community 42 - "BankApi Launch Settings"
Cohesion: 0.13
Nodes (15): ASPNETCORE_ENVIRONMENT, applicationUrl, commandName, dotnetRunMessages, environmentVariables, launchBrowser, applicationUrl, commandName (+7 more)

### Community 43 - "Host Launch Settings"
Cohesion: 0.13
Nodes (15): ASPNETCORE_ENVIRONMENT, applicationUrl, commandName, dotnetRunMessages, environmentVariables, launchBrowser, applicationUrl, commandName (+7 more)

### Community 44 - "Agent Options & Model Routing"
Cohesion: 0.14
Nodes (10): ChatClient, IChatClient, AgentOptions, Dictionary, string, TimeSpan, TracedChatClientFactory, ConcurrentDictionary (+2 more)

### Community 45 - "Semantic Financing Search"
Cohesion: 0.19
Nodes (9): Option, FinancingSummary, SemanticFinancingSearcher, CancellationToken, float, IReadOnlyList, JsonElement, Score (+1 more)

### Community 46 - "Beneficiary Endpoints"
Cohesion: 0.23
Nodes (6): BeneficiaryEndpoints, WebApplication, Beneficiary, BeneficiaryService, IBeneficiaryService, IReadOnlyList

### Community 47 - "Money Transfer Service"
Cohesion: 0.28
Nodes (5): TransferReceipt, ITransferService, TransferService, BankValidationResult, IReadOnlyDictionary

### Community 48 - "Prompt Rule Constants"
Cohesion: 0.15
Nodes (12): Beş Blok Deseni — identity/scope/operating_rules/output_contract/guardrails, InstructionPrecedence, InternalGuardrail, InternalIdAgentChannel, Kanal, Uzman Tarafında Olgu Sayılır (Bilinen Sınır), NoFabrication, NoInternalDisclosure, NoStatusChatter (+4 more)

### Community 49 - "Card Debt Collection Service"
Cohesion: 0.27
Nodes (5): PaymentReceipt, CollectionService, ICollectionService, DateOnly, IReadOnlyDictionary

### Community 50 - "LoadTest Options & Mix Parsing"
Cohesion: 0.26
Nodes (5): Dictionary, Key, TimeSpan, LoadTestOptions, Value

### Community 51 - "Topic Context Provider"
Cohesion: 0.20
Nodes (10): AIContext, AIContextProvider, InvokedContext, InvokingContext, ProviderSessionState, TopicContextProvider, CancellationToken, HashSet (+2 more)

### Community 52 - "HITL Approval Test Scenarios"
Cohesion: 0.17
Nodes (8): RequestPort — HITL Onay Kapısı, WorkflowEngine — Tek İş Akışı Motoru, WorkflowTest, Banking.Agents.Workflows.Approval, Sözleşme: Onay Kapısı (HITL), Senaryo: pay — Kart Borcu Ödeme, RequestPort HITL Kod Örneği, MAF Built-in Fonksiyon Entegrasyonu

### Community 53 - "LoadTest Runner & Leak Scanner"
Cohesion: 0.18
Nodes (4): LoadTest, JsonNode, Regex, LeakScanner

### Community 54 - "Bank Domain Models"
Cohesion: 0.24
Nodes (7): Banking.Domain.Data, Banking.Domain.Models, Banking.Domain.Validation, IbanDisplay, BankRejectionCodes, BankValidationHintKeys, string

### Community 55 - "Insurance Endpoints"
Cohesion: 0.26
Nodes (6): InsuranceEndpoints, WebApplication, InsurancePolicy, IInsuranceService, InsuranceService, IReadOnlyList

### Community 56 - "MkCode Catalog"
Cohesion: 0.26
Nodes (7): MkCodeDefinition, MkCodeCatalog, CancellationToken, ConcurrentDictionary, IReadOnlyList, JsonElement, Task

### Community 57 - "LoadTest Metrics Collector"
Cohesion: 0.23
Nodes (8): FailureKind, ConcurrentDictionary, int, IReadOnlyDictionary, IReadOnlyList, TimeSpan, MetricsCollector, StepStatistics

### Community 58 - "JSON Policy & Trace Unwrap"
Cohesion: 0.18
Nodes (7): JsonPolicy — Ortak JSON Biçimi, TracingAIFunction.Unwrap — İzde Çift Kaçış Önleme, AgentJsonPolicy, JsonSerializerOptions, JsonFormatExtensions, JsonPolicy, JsonSerializerOptions

### Community 59 - "Semantic MkCode Search"
Cohesion: 0.29
Nodes (8): Code, SemanticMkCodeSearcher, CancellationToken, float, int, IReadOnlyList, Score, Task

### Community 60 - "Card Service"
Cohesion: 0.35
Nodes (4): Card, CardService, ICardService, IReadOnlyList

### Community 61 - "Turkish IBAN Validation"
Cohesion: 0.27
Nodes (4): TurkishIbanValidationStatus, TurkishIbanValidator, int, string

### Community 62 - "Card Operations Service"
Cohesion: 0.31
Nodes (4): CardActionReceipt, CardOperationsService, ICardOperationsService, string

### Community 63 - "In-Memory Session Store"
Cohesion: 0.29
Nodes (6): IMemoryCache, InMemorySessionStore, CancellationToken, EvictionReason, TimeSpan, ValueTask

### Community 64 - "Semantic Workflow Search"
Cohesion: 0.20
Nodes (6): Metadata, SemanticWorkflowSearcher, float, IEnumerable, IReadOnlyList, Score

### Community 65 - "Agent Conversation Store"
Cohesion: 0.31
Nodes (7): AgentConversationRestore, AgentConversationStore, AgentSession, AIAgent, CancellationToken, JsonElement, Task

### Community 66 - "CardsTools MCP"
Cohesion: 0.58
Nodes (5): CardsTools, CancellationToken, Description, McpServerTool, Task

### Community 67 - "LoadTest Scenario Catalog"
Cohesion: 0.38
Nodes (5): Dictionary, List, JourneyPlan, ScenarioCatalog, ScenarioKind

### Community 68 - "Workflow Registry"
Cohesion: 0.28
Nodes (6): RegisteredWorkflow, RegisteredWorkflow, WorkflowRegistry, ConcurrentDictionary, IReadOnlyList, Task

### Community 69 - "Navigation Tools (Screen Open/Search)"
Cohesion: 0.28
Nodes (5): NavigationTools, AITool, Description, IReadOnlyList, Task

### Community 70 - "Declarative Definition Schema"
Cohesion: 0.33
Nodes (7): DeclarativeActionConfig, DeclarativeModelConfig, DeclarativeModelOptionsConfig, DeclarativeSchemaConfig, MicrosoftAgentDeclarativeDefinition, Dictionary, IReadOnlyDictionary

### Community 71 - "AccountsTools MCP"
Cohesion: 0.47
Nodes (5): AccountsTools, CancellationToken, Description, McpServerTool, Task

### Community 72 - "LoadTest Bank Health Probe"
Cohesion: 0.36
Nodes (5): CancellationToken, IEnumerable, JsonNode, Task, BankProbe

### Community 73 - "TracingAIFunction Decorator"
Cohesion: 0.29
Nodes (6): AIFunction, AIFunctionArguments, TracingAIFunction, CancellationToken, JsonElement, ValueTask

### Community 74 - "Semantic Index Threshold Design"
Cohesion: 0.38
Nodes (3): SemanticIndex — Kayıt Türüne Göre Farklı Eşik, Banking.Domain.Semantic, Banking.Domain.MkCodes

### Community 75 - "MAF Action Executor Step"
Cohesion: 0.33
Nodes (6): Executor, IWorkflowContext, DeterministicActionStep, CancellationToken, ValueTask, WorkflowOutcome

### Community 76 - "Bank API Client DI Setup"
Cohesion: 0.33
Nodes (5): IConfiguration, IServiceCollection, BankApiClientServiceCollectionExtensions, BankApiOptions, string

### Community 77 - "Candidate Workflow Search"
Cohesion: 0.33
Nodes (3): IReadOnlyList, List, WorkflowMetadata

### Community 78 - "ISessionStore Contract"
Cohesion: 0.47
Nodes (3): ISessionStore, CancellationToken, ValueTask

### Community 79 - "Session Isolation & Double-Approve Test"
Cohesion: 0.40
Nodes (4): Oturum — Tek Kapı, Tek Okuma/Tek Yazma, Sözleşme: Çift Onay, Sözleşme: Oturum İzolasyonu, Senaryo: double — Onay Ekranında Eşzamanlı İki Onay

### Community 81 - "Agent Constants"
Cohesion: 0.60
Nodes (4): AgentConstants, SpecialistNames, ToolGroups, string

### Community 82 - "BankClock"
Cohesion: 0.50
Nodes (3): Banking.Domain, BankClock, DateOnly

### Community 83 - "Session Store Options"
Cohesion: 0.50
Nodes (3): SessionStoreOptions, string, TimeSpan

## Knowledge Gaps
- **134 isolated node(s):** `net9.0`, `Microsoft.Agents.AI (1.11.1)`, `Microsoft.Agents.AI.A2A (1.11.1-preview.260625.1)`, `Microsoft.Agents.AI.Hosting (1.11.1-preview.260625.1)`, `Microsoft.Agents.AI.OpenAI (1.11.1)` (+129 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **22 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Banking.BankApi.Client` connect `MCP Namespaces & Finans Specialist` to `Bank API Client Tracing`, `Documentation Rules & Acceptance Test`, `Core Security & Routing Concepts`, `Bank API Client DI Setup`, `Semantic Financing Search`, `HITL Approval Test Scenarios`, `Declarative Workflow Concepts & Gate`?**
  _High betweenness centrality (0.175) - this node is a cross-community bridge._
- **Why does `JourneyRunner` connect `LoadTest Journey Results` to `Documentation Rules & Acceptance Test`, `LoadTest Runner & Leak Scanner`?**
  _High betweenness centrality (0.149) - this node is a cross-community bridge._
- **What connects `net9.0`, `Microsoft.Agents.AI (1.11.1)`, `Microsoft.Agents.AI.A2A (1.11.1-preview.260625.1)` to the rest of the system?**
  _134 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Workflow Action Execution & Idempotency` be split into smaller, more focused modules?**
  _Cohesion score 0.0715846994535519 - nodes in this community are weakly interconnected._
- **Should `LoadTest Journey Results` be split into smaller, more focused modules?**
  _Cohesion score 0.1376659678546471 - nodes in this community are weakly interconnected._
- **Should `Banking.Host Wiring & Trace Channel` be split into smaller, more focused modules?**
  _Cohesion score 0.059506531204644414 - nodes in this community are weakly interconnected._
- **Should `Solution & Package Manifest` be split into smaller, more focused modules?**
  _Cohesion score 0.05187074829931973 - nodes in this community are weakly interconnected._