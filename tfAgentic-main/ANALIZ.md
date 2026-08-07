# Detaylı Mimari, Akış ve Fonksiyon Dokümanı — MAF Agentic Bankacılık Asistanı

> **Teknoloji Yığını:** Microsoft Agent Framework (MAF) · .NET 9 · OpenAI / `IChatClient` · Model Context Protocol (MCP) · Agent-to-Agent (A2A) · ASP.NET Core Minimal API · Vanilla JS Web UI
>
> **Kapsam:** Bu doküman, sistemi **bir kullanıcı mesajının HTTP isteği olarak girdiği andan, yanıtın HTTP cevabı olarak çıktığı ana kadar** izlediği gerçek çağrı sırasına göre anlatır. Her sınıf ilk gerçekten çağrıldığı noktada tanıtılır; her fonksiyon tek cümlelik "ne yapar / nasıl yapar" açıklamasıyla listelenir. Önemli veri/durum tipleri (Session, Scope, StateBag vb.) ayrıca hangi alanları taşıdıkları ve neden var oldukları açıklanarak derinlemesine ele alınır.
>
> Bu belge `refactor` dalındaki güncel mimariyi yansıtır (`WorkflowEngine<TSlots>` tabanlı motor). Eski `CollectionWorkflow` / `GatewaySession` / `PaymentApprovalWorkflow` gibi sınıflar bu refactor ile kaldırılmıştır.

---

## 0. Uygulama Açılışı — `Program.cs` (Composition Root)

Herhangi bir istek gelmeden önce, süreç ayağa kalkarken şu adımlar sırayla çalışır (`src/Banking.Host/Program.cs`):

1. **JSON encoder ayarı** — Türkçe karakterlerin (`ı`, `ğ`, `ş`…) JSON yanıtlarında `\uXXXX` kaçışı yerine doğal UTF-8 çıkmasını sağlar.
2. **User Secrets yüklemesi** — `Agents:ApiKey` gizli ayarını (varsa) repo dışından okur.
3. **`AgentOptions` bağlama** — `appsettings`/User Secrets'tan `Agents` bölümünü okur, `ApiKey` boşsa `OPENAI_API_KEY` ortam değişkenine düşer.
4. **Kestrel** tüm ağ arayüzlerinde (`http://*:5080`) dinlemeye ayarlanır.
5. **Domain servisleri** singleton olarak kaydedilir: `InMemoryBankStore`, `CustomerContext`, `IAccountService`, `ICardService`, `ICollectionService`, `ITransferService`, `IAnalyzeService`, `ICampaignCatalogService`, `IFinancingCatalogService`.
6. **Semantik arama motorları** singleton kaydedilir: `LocalEmbedder`, `SemanticMkCodeSearcher`, `SemanticCampaignSearcher`, `SemanticFinancingSearcher`, `SemanticWorkflowSearcher` (dördü de ONNX tabanlı yerel embedding kullanır, dış ağa çıkmaz).
7. **`IToolRegistry`** → `ToolRegistry.Instance` (lazy singleton) DI'a bağlanır.
8. **LLM altyapısı**: `TraceHub` singleton kaydedilir; `ChatClientFactory.Create` ile ham OpenAI `ChatClient` üretilir; her ajan kurucusunun kullanacağı `Func<string, IChatClient>` fabrikası kaydedilir — bu fabrika her çağrıda ham istemciyi bir `TracingChatClient` ile sarar.
9. **Uzman/orkestratör altyapısı**: `McpToolProvider`, `SpecialistCatalog`, `NavigationTools`, `AgentRuntime` singleton kaydedilir.
10. **Oturum yönetimi**: `IMemoryCache`, `ISessionStore → InMemorySessionStore`, `ISessionAccessor → AmbientSessionAccessor` kaydedilir (bkz. Bölüm 2 — Redis'e geçiş burada tek satır değişikliktir).
11. **İş akışı motoru altyapısı**: `WorkflowAgentFactory`, `WorkflowRegistry`, `WorkflowDispatcher` kaydedilir; ayrıca `OrchestratorFallback` delegesi kaydedilir (akıştan orkestratöre devretme kanalı — döngüsel bağımlılığı önlemek için `IServiceProvider` üzerinden geç çözülür).
12. **Akış eksekütörleri**: `IDeterministicActionExecutor<PaymentSlots> → PaymentActionExecutor`, `IDeterministicActionExecutor<TransferSlots> → TransferActionExecutor`. Bir akışın **banka tarafındaki tüm temas noktaları** (ön-doğrulama + onay ekranı + eylem) bu tek arayüzdedir; akış başına ikinci bir doğrulayıcı soyutlaması yoktur.
13. **Gateway katmanı**: `FlowRouter`, `GatewayResponseMapper`, `GatewayService`, `ResponseSanitizer` singleton kaydedilir.
14. **MCP server** `/mcp` altında `AccountsTools`, `CardsTools`, `AnalyzeTools` ile açılır (loopback HTTP).
15. **A2A endpoint'leri** — `SpecialistCatalog.DiscoverCards()` ile bulunan her uzman kart için `/a2a/{route}` altında bir A2A server kaydedilir; uzman listesi hiçbir yerde elle tekrarlanmaz.
16. `app.Build()` sonrası, bir DI scope içinde **ısıtma/indeksleme** yapılır: `SemanticMkCodeSearcher.Initialize()`, `SemanticCampaignSearcher.Initialize()`, `SemanticFinancingSearcher.Initialize()`; `WorkflowRegistry.AutoDiscoverWorkflows(...)` iki assembly'yi (Banking.Domain + Banking.Agents) tarar; `declarative-agents/*.yaml|json` diskten okunup kaydedilir; `SemanticWorkflowSearcher.Initialize(...)` tüm keşfedilen akışları vektörize eder.
17. Statik dosyalar (`wwwroot/index.html`) ve HTTP endpoint'leri map edilir (aşağıda tek tek listelenmiştir).
18. `app.Lifetime.ApplicationStarted` üzerinde arka planda `AgentRuntime.WarmUpAsync()` çağrılır — orkestratör ve tüm uzmanlar ilk gerçek kullanıcı isteğinden önce ısıtılır.

### HTTP Endpoint'leri

| Endpoint | Görev |
|---|---|
| `POST /api/chat` | Basit sohbet girişi (`message`/`text` + `sessionToken`) → `GatewayService.ProcessUserRequestAsync` |
| `POST /api/ai/chat` | Java MW'ye özel gateway girişi (`GatewayRequest` şeması) → aynı `GatewayService` metodu |
| `POST /api/workflows/register-declarative` | Çalışma zamanında YAML/JSON deklaratif akış kaydı (bkz. Bölüm 6) |
| `GET /api/diag` | `AgentRuntime.GetDiagnosticsAsync` — MCP/uzman/A2A wiring'inin sağlık kontrolü |
| `GET /api/trace/stream` | `TraceHub` SSE kanalı — UI'ın canlı iz paneli buna abone olur |

---

## 1. Mesajın Girişi — `GatewayService`

Kullanıcı mesajı HTTP isteğiyle `Program.cs`'teki minimal API lambda'sına düşer, oradan doğrudan `GatewayService.ProcessUserRequestAsync`'e taşınır. **Bu sınıf, her isteğin oturum scope'unu açan tek yerdir.**

**`GatewayService`** (`src/Banking.Gateway/GatewayService.cs`) — İsteklerin giriş/çıkış koordinatörü; bağımlılıkları: `FlowRouter`, `TraceHub`, `ILogger`, `ResponseSanitizer`, `GatewayResponseMapper`, `ISessionAccessor`.

- `ProcessUserRequestAsync(request, ct)`: Gelen isteğin session token'ını normalize eder ve tüm işi `ISessionAccessor.RunInSessionScopeAsync` içine bir delege olarak devreder — bu satırdan sonrasının tamamı oturum kilidi altında çalışır.
- `HandleInSessionAsync(request, sessionToken, ct)`: Scope içindeyken çalışır; `FlowRouter.RouteUserRequestAsync`'i çağırır, sonucu `GatewayResponseMapper.Map` ile UI protokolüne çevirir, hata/timeout durumlarını yakalayıp kullanıcıya güvenli bir mesaj döner (`try/catch OperationCanceledException` ve genel `Exception`).
- `FinalizeResponse(response, sessionToken)`: **Tüm yanıtların tek çıkış noktası.** Yanıta session token'ı ekler, metni varsa `ResponseSanitizer.Sanitize` ile temizler.
- `NormalizeSessionToken(token)`: Token boşsa `gw-` önekli rastgele bir token üretir, doluysa olduğu gibi kullanır.
- `Truncate(text, maxLength)`: Log/trace çıktısında uzun metni `…` ile kısaltan yardımcı.

---

## 2. Oturum Açılışı — Session, Scope ve StateBag (derinlemesine)

Bu bölüm, projedeki en kritik ve en sık karıştırılan üç kavramı ayırt eder: **Session (veri)**, **Scope (yaşam döngüsü/kilit mekanizması)** ve **StateBag (slot değerleri sözlüğü)**. Üçü de `src/Banking.Agents/Sessions/` ve `Workflows/Slots/` altında yaşar.

### 2.1 `SessionState` — ham veri (StateBag'in de içinde yaşadığı yer)

**`SessionState`** (`Sessions/SessionState.cs`) — Bir kullanıcı oturumuna ait **tüm** veriyi taşıyan, davranışsız (saf) sınıf. Ajanlar singleton/stateless olduğu için kullanıcıya özel her şey burada durur.

| Alan | Tip | Neden var |
|---|---|---|
| `SessionId` | `string` (required) | Oturumun kimliği; `ISessionStore`'daki cache anahtarının kökü. |
| `ActiveWorkflowName` | `string?` | Hangi `[Workflow]` akışının aktif olduğu; yoksa `null`. |
| `Phase` | `string` (bkz. `WorkflowPhases`) | Akışın 5 evreden hangisinde olduğu: `idle/slot_filling/awaiting_approval/completed/cancelled`. |
| **`SlotValues`** | `Dictionary<string, object?>` | **İşte "StateBag" budur** — trace mesajlarında da bu ad geçer ("StateBag Parametre Eklendi" vb.). Kullanıcıdan `update_slot` aracıyla toplanan ham (henüz tipe dönüştürülmüş/dönüştürülmemiş) parametre değerlerini anahtar/değer olarak tutar. |
| `ProposedSlots` | `object?` | Tüm slotlar tamamlanınca kurulan tipli slot nesnesi (örn. `PaymentSlots`) ya da deklaratif akışlarda sözlük. |
| `PendingConfirmation` | `GenericConfirmationView?` | Onay ekranına gönderilen görünüm; onay beklenirken dolu. |
| `IdempotencyToken` | `string?` | Onaya sunulan slot kümesinin SHA-256 parmak izi; çift onayı (çift tıklama) engeller. |
| `CompletedOutcome` | `WorkflowOutcome?` | Bu idempotency anahtarı için üretilmiş sonuç; tekrar çalıştırmayı engeller. |
| `SerializedOrchestratorSession` / `SerializedWorkflowSession` | `JsonElement?` | Orkestratör/akış ajanının serileştirilmiş konuşma geçmişi (MAF `AgentSession`). |
| `LastUserMessage` | `string?` | Akış başlatma aracının kullandığı, kullanıcının o turdaki cümlesi. |
| `LastUpdatedAt` | `DateTimeOffset` | TTL/tazeleme için; `ISessionStore.SaveAsync` her yazımda günceller. |

**Neden StateBag ayrı bir sınıf değil de bir `Dictionary`?** Çünkü her akış (`PaymentSlots`, `TransferSlots`, gelecekteki her yeni akış) farklı alanlar ister; sabit şemalı bir sınıf her yeni akışta değişmek zorunda kalırdı. Sözlük + reflection (`SlotBinder`, bkz. Bölüm 5.2) kombinasyonu, yeni bir akış eklendiğinde bu katmanın **hiç değişmemesini** sağlar.

### 2.2 `SessionContext` — session'a giden **tek kapı**

**`SessionContext`** (`Sessions/SessionContext.cs`) — `SessionState`'in üzerine konmuş, senkron API sunan ince kabuk. `internal` constructor'ı sayesinde yalnızca `AmbientSessionAccessor` üretebilir; uygulamanın geri kalanı `SessionState`'e asla doğrudan dokunmaz.

- `SessionId`: `State.SessionId`'yi döner.
- `ActiveWorkflowName` / `Phase` (get/set): Akış durumunu okur/yazar.
- `HasActiveWorkflow`: Akış adı doluysa VE evre `SlotFilling` ya da `AwaitingApproval` ise `true` — yani "kullanıcı etkileşimi bekleyen" bir akış var mı sorusuna cevap verir.
- `LastUserMessage` (get/set): O turdaki kullanıcı mesajını taşır.
- `BeginWorkflow(workflowName)`: Akışı bu oturuma bağlar, evreyi `Idle`'a çeker.
- `CompleteWorkflow()` / `CancelWorkflow()`: Akışı sonlandırıp StateBag'i (`SlotValues`, `ProposedSlots`, `PendingConfirmation`, `IdempotencyToken`, `CompletedOutcome`, `SerializedWorkflowSession`) temizler (`ResetWorkflow` yardımcısı üzerinden).
- `SetSlot(slotName, slotValue)`: **StateBag'e tek bir değer yazar** — `update_slot` aracının arkasındaki gerçek yazım noktası.
- `GetSlotStatus<TSlots>()`: `SlotBinder.GetStatus` çağırıp dolu/eksik slot listesini döner; tarih alanlarını sistem tarihinden otomatik doldurur.
- `ConstructSlots<TSlots>()`: StateBag'deki ham değerlerden tipli `TSlots` nesnesini kurmayı dener (`SlotBinder.Construct`); dönüşemeyen alanı StateBag'den siler ki kullanıcı aynı hatayı sonsuza kadar tekrarlamasın.
- `StoreProposedSlots(workflowName, slots)`: Kurulmuş slot nesnesini onaya hazır olarak saklar VE alanlarını tekrar StateBag'e yazar (bir sonraki tur aynı değerleri görsün diye).
- `DeclareExpectedSlots(slotNames)`: Deklaratif akışlarda beklenen anahtarları şablon olarak tanımlar (yalnızca `ProposedSlots` boşken).
- `ReadProposedSlots<TSlots>()`: Onaya sunulmuş nesneyi `TSlots` olarak döner; tip uymuyorsa `null`.
- `PendingConfirmation` / `IdempotencyToken` / `CompletedOutcome` (get/set): İlgili `SessionState` alanlarına doğrudan erişim.
- `OrchestratorConversation` / `WorkflowConversation` (get/set): Serileştirilmiş konuşma geçmişlerine erişim.
- `ResetWorkflow(finalPhase, traceMessage)` (private): Akışı sonlandıran ortak temizlik mantığı; `CompleteWorkflow`/`CancelWorkflow` bunu çağırır.

**Neden bu metotlar senkron?** Çünkü veri zaten bellekte — scope açılışında **bir kez** yüklenmiştir, burada I/O yoktur.

### 2.3 Scope — `ISessionAccessor` / `AmbientSessionAccessor`

**`ISessionAccessor`** (`Sessions/ISessionAccessor.cs`) — Aktif isteğin oturumuna erişim sözleşmesi.

- `Current` (property): Scope içindeyse aktif `SessionContext`'i döner; scope dışında çağrılırsa `InvalidOperationException` fırlatır (sessizce yeni oturum üretmek iki kullanıcının verisini karıştırma riski taşır).
- `RunInSessionScopeAsync<TResult>(sessionId, operation, ct)`: İşi bir oturum scope'u içinde çalıştırır — durum **bir kez** yüklenir, oturuma özel kilit alınır, `operation` çalıştırılır, iş bitince durum **bir kez** yazılır ve kilit bırakılır.

**`AmbientSessionAccessor`** (`Sessions/AmbientSessionAccessor.cs`) — `AsyncLocal<SessionContext?>` tabanlı tek uygulama; bağımlılıkları `ISessionStore` ve `TraceHub`.

- `Current`: `_currentSession` (AsyncLocal) doluysa döner, değilse patlar.
- `RunInSessionScopeAsync(sessionId, operation, ct)`:
  1. Eğer aynı `sessionId` ile zaten bir scope açıksa (iç içe çağrı — örn. LLM aracı → dispatcher zinciri), yeniden yükleme/kilitleme yapmadan doğrudan `operation`'ı çalıştırır.
  2. Farklı bir `sessionId` ile scope açıksa `InvalidOperationException` fırlatır — bu, oturum izolasyonunun ihlalini (bir isteğin ortasında başka bir kullanıcının verisine geçilmesini) engelleyen tek kontrol noktasıdır.
  3. Hiç scope yoksa: `AcquireLockHandle` ile `sessionId`'ye özel `SemaphoreSlim`'i alır (çift tıklamayı sıraya sokar) → `ISessionStore.TryGetAsync` ile durumu **bir kez** okur (yoksa yeni `SessionState` üretir) → `SessionContext` kurup `AsyncLocal`'a yazar → `operation`'ı çalıştırır → `finally`'de `ISessionStore.SaveAsync` ile **bir kez** yazar ve `AsyncLocal`'ı temizler → en dıştaki `finally`'de kilidi bırakır.
- `AcquireLockHandle(sessionId)` / `ReleaseLockHandle(sessionId, sessionLock)` (private): Oturum başına referans sayaçlı kilit tablosu yönetimi; sayaç sıfırlanınca kilit tablodan silinir (aksi hâlde sınırsız büyürdü).

**Neden `BeginScopeAsync`/`EndScopeAsync` gibi iki ayrı metotla değil de tek bir delege-tabanlı metotla?** Çünkü `AsyncLocal<T>` içindeki değişiklikler bir `async` metottan çağırana **geri yayılmaz**. Açılış ile kapanış tek metotta (`try/finally`) tutulmazsa "açtım ama kapatmayı unuttum" veya "yazmadan çıktım" durumu mümkün olurdu.

### 2.4 Kalıcılık — `ISessionStore` / `InMemorySessionStore`

**`ISessionStore`** (`Sessions/ISessionStore.cs`) — Oturum durumunun kalıcılık soyutlaması; **yalnızca `AmbientSessionAccessor` tarafından çağrılır.**

- `TryGetAsync(sessionId, ct)`: Oturumu döner, yoksa `null`; bulursa TTL'i tazeler.
- `SaveAsync(state, ct)`: Değiştirilmiş oturumu yazar ve TTL'i tazeler.

Arayüzde **silme metodu yoktur**: oturum sonlandırma `SessionContext.CompleteWorkflow()`/`CancelWorkflow()` ile durumu temizlemek üzerinden yapılır, kaydın kendisini TTL düşürür.

**`InMemorySessionStore`** (`Sessions/InMemorySessionStore.cs`) — `IMemoryCache` tabanlı uygulama; bağımlılıkları `IMemoryCache`, `TraceHub` ve `IOptions<SessionStoreOptions>`.

- Kayan TTL `SessionStoreOptions.SlidingTimeToLive`'dan gelir (`Sessions:SlidingTimeToLiveMinutes`, varsayılan 30 dk). **Pencere bir kurtarma süresi değil, kullanıcının düşünme süresidir:** aktif akışın tamamı — parametre soruları, kart seçimi, onay ekranının okunması — bu pencerenin içinde geçer. Kullanıcının iki mesajı arasındaki duraklamadan kısa bir değer, yarım kalan akışı sessizce düşürür.
- `TryGetAsync` / `SaveAsync`: `IMemoryCache.TryGetValue`/`Set`'i `banking-session:{sessionId}` anahtarıyla sarar.
- `PublishEviction` (private): Tahliye edilen oturumu `SYSTEM` kategorisine yazar; aktif akış varsa hangi akışın ve hangi evrenin kaybolduğunu belirtir. Depo boş döndüğünde `AmbientSessionAccessor` sessizce yeni durum kurduğu için, süresi dolmuş oturumla hiç var olmamış oturumu ayırt eden **tek** kayıt budur.
- `BuildCacheKey(sessionId)` (private): Anahtar ön eki üretir (cache başka amaçlarla da paylaşılabileceği için çakışmayı önler).

**Redis'e geçiş:** `Program.cs`'de tek satır (`AddSingleton<ISessionStore, ...>`) değişir; `SessionContext`, `AmbientSessionAccessor` ve üstündeki her şey **hiç değişmez** — imzaların `ValueTask` dönmesinin sebebi budur.

---

## 3. Yönlendirme Kararı — `FlowRouter`

Scope açıldıktan hemen sonra `GatewayService.HandleInSessionAsync`, işi `FlowRouter`'a devreder.

**`FlowRouter`** (`src/Banking.Gateway/FlowRouter.cs`) — Deterministik yönlendirici; bağımlılıkları `AgentRuntime`, `WorkflowDispatcher`, `ISessionAccessor`, `TraceHub`, `ILogger`.

- `RouteUserRequestAsync(userMessage, ct)`: `workflowDispatcher.HasActiveWorkflow` kontrolü yapar.
  - **`true`** ise: mesaj **hiçbir koşulda orkestratöre uğramadan** `WorkflowDispatcher.ContinueWorkflowAsync`'e gider (kullanıcı ödeme ortasındayken LLM akıl yürütmesiyle akıştan çıkarılamaz).
  - **`false`** ise: `AgentRuntime.AskOrchestratorAsync` çağrılır; çağrı sonrası akış yeni başlamışsa bunu iz olarak yayınlar.
  - Sonuç her iki durumda da `FlowExecutionResult` (`ExecutionType`, `WorkflowResult`, `OrchestratorReply`) olarak sarılıp döner.

Buradan itibaren akış ikiye ayrılır: **Yol A (Bölüm 4, orkestratör)** veya **Yol B (Bölüm 5, aktif iş akışı)**.

---

## 4. Yol A — Aktif Akış Yokken: Orkestratör

### 4.1 `AgentRuntime` — orkestratörün tek giriş noktası

**`AgentRuntime`** (`src/Banking.Agents/Runtime/AgentRuntime.cs`) — Bağımlılıkları: `AgentOptions`, `TraceHub`, `ILogger`, `IServiceProvider`, `SpecialistCatalog`, `NavigationTools`, `McpToolProvider`, `ISessionAccessor`, `Func<string, IChatClient>`.

- `AskOrchestratorAsync(userMessage, ct)`: `session.LastUserMessage`'ı günceller ve `AskLanguageModelAsync`'i çağırır. Aktif akış varsa (yalnızca `handoff_*` yoluyla gelinir) aday listesi **boş** geçilir; aksi hâlde `SearchCandidateWorkflows` ile bulunan adaylar bağlam olarak verilir. **Akışı bu metot başlatmaz:** başlatma kararı orkestratörün `start_workflow` aracını çağırmasıyla oluşur.
- `GetDiagnosticsAsync(ct)`: Orkestratörü/uzmanları ısıtıp API anahtarı, model, MCP araç sayısı, uzman listesi ve taşıma tipini (`A2A`/`in-process`) döner (`/api/diag`).
- `WarmUpAsync(ct)`: Orkestratörü ve tüm uzmanları önceden kurar (cold-start önleme; startup'ta çağrılır).
- `GetSpecialist(specialistName)`: A2A endpoint kaydı için `SpecialistCatalog.GetSpecialist`'e delege eder.
- `EnsureOrchestratorAsync(ct)`: Orkestratör ajanını **bir kez** kurar (çift kontrollü kilitleme — `SemaphoreSlim`); araçları toplar (`SpecialistCatalog.BuildBridgeTools` + `BuildStartWorkflowTool` + `NavigationTools.BuildTools`), sabit sistem prompt'unu (`AgentPrompts.GetOrchestratorInstructions`) ve `TopicContextProvider`'ı bağlar.
- `AskLanguageModelAsync(session, userMessage, candidateWorkflows, ct)` (private): Orkestratörü garanti eder; varsa aday akışları sistem mesajı olarak ekler (`AgentPrompts.BuildCandidateWorkflowsContext`); `ResumeOrCreateAgentSessionAsync` ile konuşmayı geri yükler; `_orchestrator.RunAsync` ile LLM'i çalıştırır; konuşmayı `session.OrchestratorConversation`'a serileştirir; yanıttaki son fonksiyon çağrısını (`FunctionCallContent`) ayıklayıp `OrchestratorReply` olarak döner.
- `ResumeOrCreateAgentSessionAsync(session, ct)` (private): `session.OrchestratorConversation` varsa deserialize eder, yoksa/bozuksa yeni `AgentSession` açar.
- `SearchCandidateWorkflows(userMessage)` (private): `SemanticWorkflowSearcher.Search(topK: 3)` sonucunun metadata'sını döner. **Eşik veya rakiple fark koşulu yoktur ve akış başlatmaz.** Vektör benzerliği konu yakınlığını ölçer, niyeti değil: aynı konudaki bilgi sorusu ile işlem talebi aynı sözcükleri taşır ve benzer skor alır ("Kartımın kullanılabilir limiti nedir?" ↔ limit artırım akışı). Aramanın kazandırdığı şey karar değil kapsam daraltmadır; akış adları sistem talimatına gömülmediği için çalışma zamanında eklenen akışlar da anında yönlendirilebilir kalır.
- `StartWorkflowDirectlyAsync(workflow, ct)` (private): Semantik eşleşen akışı doğrudan `StartWorkflowAsync`'e vererek başlatır ve `OrchestratorReply` olarak sarar.
- `BuildStartWorkflowTool()` (private): `start_workflow` MCP-tarzı aracını (`AIFunctionFactory.Create`) `TracingAIFunction` ile sararak üretir.
- `StartWorkflowAsync(workflowName, ct)` (private): `session.LastUserMessage`'ı ilk mesaj olarak kullanır; `IServiceProvider`'dan **geç çözülen** `WorkflowDispatcher`'ı alıp `StartWorkflowAsync`'i çağırır (bkz. Bölüm 5) ve ilk soruyu döner.
- `BuildSpecialistRoutingRules()` (private, static): Her `AgentCard`'ın açıklamasından orkestratör prompt'undaki yönlendirme kurallarını üretir.

**`AgentOptions`** (`Runtime/AgentOptions.cs`) — Yapılandırma tipi. Alanları: `Provider` (varsayılan `"openai"`), `Model` (`gpt-4o-mini`), `Temperature` (0.1), `ApiKey`, `Endpoint` (opsiyonel proxy), `SelfBaseUrl` (loopback taban adresi), `UseA2AForSpecialists` (bool). `McpUrl`/`A2AUrl(agent)` hesaplanan property'lerdir; `HasUsableKey` anahtarın gerçek (dummy olmayan) olup olmadığını söyler.

**`ChatClientFactory`** (`Runtime/ChatClientFactory.cs`)
- `Create(options)`: Sağlayıcı `"openai"` değilse hata fırlatır; anahtar boşsa `"dummy"` ile (wiring testi için) `ChatClient` üretir; `Endpoint` verilmişse özel adrese bağlanır.

**`DeferredSpecialistAgent`** (`Runtime/DeferredSpecialistAgent.cs`) — `AIAgent`'ı tembel kuran sarmalayıcı; startup'ta A2A endpoint map edilirken MCP'ye erken bağlanıp "connection refused" almayı önler.
- `InnerAsync(ct)` (private): İç ajanı (gerekiyorsa) kilit altında ilk çağrıda kurar, sonra önbellekten döner.
- `EnsureBuiltAsync(ct)`: İç ajanı önceden kurar (ısıtma/tanı amaçlı).
- `GetService(serviceType, serviceKey)`: A2A kartı üretilirken iç ajanı kurmadan yalnızca metadata döner.
- `CreateSessionCoreAsync` / `DeserializeSessionCoreAsync` / `SerializeSessionCoreAsync` / `RunCoreAsync` / `RunCoreStreamingAsync`: `AIAgent`'ın soyut çekirdek metotlarını, iç ajanı garanti edip ona delege ederek uygular.

**`NavigationTools`** (`Runtime/NavigationTools.cs`) — Ekran yönlendirme araçları; bağımlılıkları `SemanticMkCodeSearcher`, `TraceHub`, `ILogger`.
- `BuildTools()`: `ekran_kodu_ara` ve `ekran_ac` araçlarını `TracingAIFunction` ile sarıp döner; ad uyumunu `EnsureOpenScreenParameterName` ile başlangıçta doğrular.
- `SearchScreenCode(userIntent)` (private): `SemanticMkCodeSearcher.Search` ile en uygun ekran kodlarını skorlarıyla döner.
- `OpenScreen(targetMkCode)` (private): Hedef ekranı "açar" (metinsel onay döner); gerçek yönlendirme UI tarafında `OPEN_SCREEN` yanıt tipiyle olur.
- `EnsureOpenScreenParameterName()` (private, static): `OpenScreen` metodunun parametre adının `ToolNames.OpenScreenTargetParameter` ile birebir eşleştiğini reflection ile doğrular; eşleşmezse başlangıçta patlar (sessiz bozulmayı önler).

### 4.2 Uzmanlara Yönlendirme — `SpecialistCatalog` ve Uzman Ajanlar

Orkestratör LLM'i bir uzmana danışma kararı verirse (`hesap_uzmanina_sor` / `kart_uzmanina_sor` / `analiz_uzmanina_sor` köprü aracı), çağrı şu zincirden geçer:

**`AgentCard`** (`Specialists/AgentCard.cs`) — Bir uzmanın **tek doğruluk kaynağı** olan kimlik kartı: `Name`, `Description`, `Instructions`, `ToolGroup`, `BridgeToolName`, `A2ARoute`, `DisplayName`.

**`SpecialistCatalog`** (`Specialists/SpecialistCatalog.cs`) — Bağımlılıkları: `AgentOptions`, `McpToolProvider`, `Func<string, IChatClient>`, `TraceHub`, `ILogger`.
- `DiscoverCards()` (static): Assembly'de static `AgentCard Card` özelliği olan her sınıfı reflection ile bulup kartlarını döner (`Program.cs`'deki A2A endpoint kaydı da bunu kullanır).
- `GetSpecialist(specialistName)`: İsme (veya kısmi ada) göre `DeferredSpecialistAgent`'ı döner.
- `BuildBridgeTools()`: `AgentOptions.UseA2AForSpecialists` `true` ise her kart için `A2AClient` tabanlı HTTP köprü aracı kurmayı dener; bağlantı hatasında **otomatik olarak in-process bağlantıya düşer**; `Transport` alanını günceller.
- `WarmUpAsync(ct)`: Tüm uzmanları `EnsureBuiltAsync` ile önceden kurar.
- `BuildBridgeTool(card, specialistAgent, transportLabel, endpointInfo)` (private): Uzman ajanı `AsAIFunction` ile bir araca çevirip `TracingAIFunction` ile sarar.
- `DiscoverSpecialists(agentFactory)` / `DiscoverCardsWithTypes()` (private, static): Reflection ile kart+tip eşleşmelerini bulur.
- `BuildFromCardAsync(card, agentType, ...)` (private, static): Kartın `ToolGroup`'undaki MCP araçlarını (`McpToolProvider.GetTracedToolsForGroupAsync`) çeker, uzmanın static `Build(IChatClient, IList<AITool>)` metodunu reflection ile çağırır.

**`HesapUzmaniAgent`** / **`KartUzmaniAgent`** / **`AnalizUzmaniAgent`** (`Specialists/*.cs`) — Her biri yalnızca bir static `Card` property'si ve bir static `Build(chat, tools)` fabrika metodu içerir; sırasıyla `ToolGroup.Account` / `ToolGroup.Card` / `ToolGroup.Analyze` araç grubunu alır ve `AgentPrompts` sabitlerinden gelen talimatla `chat.AsAIAgent(...)` çağırır.

### 4.3 MCP Bağlantısı — `McpToolProvider`

**`McpToolProvider`** (`Mcp/McpToolProvider.cs`) — Süreç-içi MCP server'a bağlanan **tek** istemci; bağımlılıkları `AgentOptions`, `TraceHub`, `ILogger` (+ `ToolRegistry.Instance`). Bağlantı tembel ve tek seferliktir.
- `DiscoveredToolCount`: Bağlantı kurulduysa keşfedilen toplam araç sayısı (tanı ekranı için).
- `GetTracedToolsForGroupAsync(group, ct)`: Bağlantıyı garanti eder, `IToolRegistry.GetNamesByGroup` ile gruba ait araçları süzer ve her birini `TracingAIFunction` ile sarar (izleme + blast-radius sınırlaması).
- `EnsureConnectedAsync(ct)` (private): İlk çağrıda `HttpClientTransport` + `McpClient.CreateAsync` + `ListToolsAsync` ile bağlanır; ardından `ValidateDiscoveredMcpTools` ile **fail-fast** doğrulama yapar (registry ile canlı server araç listesi ayrışırsa burada patlar).
- `DisposeAsync()`: MCP client bağlantısını ve bağlantı kilidini serbest bırakır.

### 4.4 Bağlamsal Devamlılık — `TopicContextProvider`

**`TopicContextProvider`** (`ContextProviders/TopicContextProvider.cs`) — `AIContextProvider` uygulaması; orkestratörün `AIContextProviders` listesine eklenir.
- `ContextResettingTools` (private, static): Tetiklendiğinde konuyu kapatan araç adları — `ToolNames.StartWorkflow` ve `ToolNames.OpenScreen`.
- `ProvideAIContextAsync(context, ct)`: Oturumun `TopicState.LastAssistantResponse`'u doluysa, bunu "bir önceki turda şu söylendi" şeklinde sistem mesajı olarak enjekte eder (zamirle gelen takip sorularının doğru uzmana gitmesini sağlar).
- `StoreAIContextAsync(context, ct)`: Turun yanıt mesajlarındaki `FunctionCallContent`'lere bakar; `ContextResettingTools`'tan biri çağrıldıysa bağlamı **temizler**, aksi hâlde yanıt metnini bir sonraki tur için saklar.

> **Karar neden araç adından okunuyor:** eskiden yanıt metninde kelime aranıyordu (`"tamamlandı"`, `"iptal edildi"`, `"CONFIRM_PAYMENT"`…). Kullanıcının normal bir cevabında o kelime geçtiğinde bağlam sebepsiz siliniyordu. Ayrıca akış tamamlanma/iptal turları `FlowRouter` tarafından orkestratör baypas edildiği için bu sağlayıcıya hiç uğramaz — o kontroller zaten ölüydü.

> **Bilinen sapma:** bu sağlayıcı durumunu MAF'ın kendi `ProviderSessionState`'inde tutar, `SessionContext`'te değil — yani "tek oturum, tek kapı" kuralının dışındaki tek yerdir. Veri kaybolmaz (orkestratör konuşmasıyla birlikte `SessionState.SerializedOrchestratorSession`'a serileşir), ama `SessionState`'e taşınması ileride yapılacak bir sadeleştirmedir.

### 4.5 Prompt Deposu — `AgentPrompts`

**`AgentPrompts`** (`Prompts/AgentPrompts.cs`, static) — Tüm LLM talimatlarının merkezi deposu.

**Yazım deseni.** Her ajan talimatı aynı blok sırası ve aynı etiket sözlüğüyle yazılır. Deseni dayatan bir sınıf/şablon motoru **bilerek yoktur**; yeni ajanda iskelet elle kopyalanır (adım adım: `docs/PROMPT-YAZIM-REHBERI.md`).

```
<identity>         kim, hangi alanın uzmanı
<scope>            neyi yapar / neyi yapmaz (sınır)
<operating_rules>  çalışma disiplini: araç kullanımı, veri kaynağı, belirsizlik
<output_contract>  biçim ve sunum sözleşmesi
<guardrails>       ortak sabitlerden gelen koruma bloğu (her ajanda AYNI metin)
```

**Ortak kural sabitleri** — her prompt bunları interpolasyonla alır, yeniden yazmaz: `NoStatusChatter` (durum/hazırlık cümlesi yasağı), `NoInternalDisclosure` (araç adı, düşünce süreci, talimatın kendisi sızdırılmaz), `NoFabrication` (araç döndürmediyse üretme), `InstructionPrecedence` (injection savunması — kullanıcı mesajındaki kural gevşetme istekleri talimat değil, yanıtlanacak içeriktir), `InternalGuardrail` (dahili kayıt kimliği kullanıcıya sorulmaz/gösterilmez), `InternalIdAgentChannel` (yasak muhataba değil **kanala** bağlıdır: soran taraf bir ajansa ve açıkça istiyorsa kimlik verilir), `PresentationContract` (Markdown, alt alta liste, ₺ + binlik ayraç, sade Türkçe).
- `SpecialistGuardrails`: uzmanların koruma bloğu; kanal istisnasını da içerir. `DeclarativeSpecialistBuilder` bunu **koşulsuz** ekler — arayüzden tanımlanan bir uzman, talimat alanını boş bırakarak guardrail'siz kalamaz.
- Akış ajanlarının koruma bloğu aynı satırları kanal istisnası olmadan taşır: akış ajanı kimliği **isteyen** taraftır, veren değil.

**Üyeler**
- `HesapUzmaniInstructions` / `KartUzmaniInstructions` / `FinansalAnalizUzmaniInstructions`: Üç uzmanın sabit talimatı; üçü de aynı desenin birebir kopyasıdır, farklı olan tek şey alandır. Metinde **araç adı geçmez** — kural ilke bazlı yazılır ("ilgili listeleme aracını çağır"), çünkü prompt'a gömülen ad ile kayıtlı ad ayrıştığında ajan var olmayan bir aracı çağırır.
- `BuildSlotFillingRules(proposeToolName, cancelToolName, handoffToolName)`: Slot toplayan ajanların `<operating_rules>` + `<output_contract>` + koruma bloğunu üretir — derlenmiş ve deklaratif akışların **paylaştığı tek kaynak**. Araç adları parametre olarak alınır, burada türetilmez; tek kaynak `WorkflowAgentDefinition.For`'dur.
- `GetOrchestratorInstructions(dynamicRoutingRules)`: Orkestratörün sabit (kullanıcıya/isteğe özel bilgi içermeyen) sistem prompt'unu üretir. **Hiçbir akış adı saymaz** — akışlar çalışma zamanında eklenip kaldırılabilir; araç adları `ToolNames` sabitlerinden, uzman yönlendirme kuralları kartlardan (`AgentRuntime.BuildSpecialistRoutingRules`) gelir.
- `BuildCandidateWorkflowsContext(candidateWorkflows)`: Semantik aramanın bulduğu adayları, orkestratöre **çalışma anında mesaj olarak** geçirilecek `<candidate_workflows>` bağlamına çevirir (sistem prompt'una gömülmez ki orkestratör tek seferlik kurulabilsin). Liste `- workflowName: 'x' — açıklama` biçimindedir: modelin `start_workflow`'a yazacağı anahtar adı listede görünür, tahmine bırakılmaz.

### 4.6 İzleme Katmanı — `TraceHub` / `TracingAIFunction` / `TracingChatClient`

**`TraceHub`** (`Tracing/TraceHub.cs`) — Tüm katmanlardan gelen iz olaylarının merkezi yayıncısı; `ConcurrentDictionary<Guid, Channel<string>>` ile SSE aboneliklerini tutar.
- `Subscribe()` / `Unsubscribe(id)`: SSE bağlantısı açar/kapatır (`/api/trace/stream` bunu kullanır).
- `Publish(layer, text, detail)`: Olayı hem `ILogger`'a hem de bağlı tüm SSE kanallarına JSON olarak yazar; detay 1000 karakteri geçerse kısaltır.

**`TracingAIFunction`** (`Tracing/TracingAIFunction.cs`) — Bir `AIFunction`'ı sarıp her çağrıyı iz olarak yayınlayan dekoratör.
- `InvokeCoreAsync(arguments, ct)`: Çağrı öncesi parametreleri, çağrı sonrası sonucu `TraceHub.Publish` ile yayınlar, ardından asıl aracı çalıştırır.

**`TracingChatClient`** (`Tracing/TracingChatClient.cs`) — Bir `IChatClient`'ı saran `DelegatingChatClient`; her ajan kurulumunda `Func<string, IChatClient>` fabrikası bunu üretir.
- `GetResponseAsync(messages, options, ct)`: Sıcaklığı (`temperature`) varsayılana sabitler, son mesaj özetini ve yanıt özetini `TraceHub`'a yayınlar, asıl çağrıyı yapar.
- `GetStreamingResponseAsync(...)`: Aynı izlemeyi streaming yanıtlar için de yapar.
- `GetMessageSummary(msg)` / `GetResponseSummary(response)` (private, static): Mesaj/yanıt içeriğinden (metin, araç çağrısı, araç sonucu) kısa bir özet çıkarır.

---

## 5. Yol B — Aktif Akış: `WorkflowDispatcher` → `WorkflowEngine<TSlots>`

`FlowRouter`, `HasActiveWorkflow == true` bulduğunda ya da orkestratör `start_workflow` aracını tetiklediğinde akış buraya girer.

### 5.1 Giriş Kapısı — `WorkflowDispatcher`

**`WorkflowDispatcher`** (`Workflows/Engine/WorkflowDispatcher.cs`) — Bağımlılıkları: `WorkflowRegistry`, `ISessionAccessor`. **Kendi kilidi veya depo erişimi yoktur** — bunlar zaten scope'un işidir.
- `HasActiveWorkflow`: `sessionAccessor.Current.HasActiveWorkflow`'u döner.
- `StartWorkflowAsync(workflowName, initialUserMessage, ct)`: Boş ad kabul etmez (`ArgumentException`); `session.BeginWorkflow` çağırır; `WorkflowRegistry.CreateEngineAsync` ile motoru üretir; `engine.AdvanceAsync(initialUserMessage)` ile ilk adımı çalıştırır.
- `ContinueWorkflowAsync(userMessage, ct)`: Aktif akış adı yoksa (TTL dolmuş) dürüst bir "süresi doldu" mesajı döner; varsa motoru üretip `AdvanceAsync(userMessage)` çağırır.

### 5.2 Katalog ve Motor Fabrikası — `WorkflowRegistry`

**`WorkflowRegistry`** (`Workflows/Engine/WorkflowRegistry.cs`) — Bağımlılıkları: `IServiceProvider`, `WorkflowAgentFactory`, `ISessionAccessor`, `TraceHub`.
- `AutoDiscoverWorkflows(slotAssembly, executorAssembly)`: `slotAssembly`'de `[Workflow]` etiketli tüm record'ları bulup `_discoveredWorkflows`'a ekler; `executorAssembly`'de `IDeterministicActionExecutor<TSlots>` uygulayan sınıfların hizmet ettiği `TSlots` tiplerini bulup her biri için `RegisterCompiledWorkflow<TSlots>`'u reflection ile çağırır.
- `RegisterDeclarativeWorkflow(definition)`: Aynı ad derlenmiş bir akışa aitse `InvalidOperationException` fırlatır (para hareketi yapan eksekütörün bir YAML ile sessizce ezilmesini engeller); değilse akış adını metadata'ya ve `_engineFactories`'e ekler — fabrika, gerektiğinde `WorkflowAgentFactory.GetOrCreateAsync<Dictionary<string,object?>>` ile ajanı kurup `DeclarativeActionExecutor` ile bir `WorkflowEngine<Dictionary<string,object?>>` üretir.
- `CreateEngineAsync(workflowName)`: Kayıtlı fabrikayı çalıştırıp `IWorkflowEngine` döner; kayıtlı değilse `KeyNotFoundException`.
- `RegisterCompiledWorkflow<TSlots>(workflowName)` (private): Fabrikayı kaydeder — çağrıldığında `WorkflowAgentFactory.GetOrCreateAsync<TSlots>` ile ajanı kurar, DI'dan `IDeterministicActionExecutor<TSlots>`'u çeker ve `WorkflowEngine<TSlots>` üretir.
- `ResolveOrchestratorFallback()` (private): `OrchestratorFallback` delegesini **geç** (her seferinde `IServiceProvider`'dan) çözer — yapıcıda çözülseydi orkestratör↔akış döngüsel bağımlılığı oluşurdu.
- `DiscoverExecutorSlotTypes(targetAssembly)` (private, static): Assembly'deki `IDeterministicActionExecutor<T>` uygulamalarının `T` tipini reflection ile çıkarır.

### 5.3 Ajan Kurucusu — `WorkflowAgentFactory`

**`WorkflowAgentFactory`** (`Workflows/Engine/WorkflowAgentFactory.cs`) — Bağımlılıkları: `McpToolProvider`, `ISessionAccessor`, `IServiceProvider`, `Func<string, IChatClient>`, `TraceHub`.
- `GetOrCreateAsync(definition, ct)`: Ajanı adına göre önbellekten döner; talimat metni ya da uzman listesi değiştiyse yeniden kurar (önbellek anahtarı: ad + talimat parmak izi + `SpecialistCatalog.Version`) — **ajanlar stateless olduğu için tek örnek her oturuma yeter.** Talimatı kendisi üretmez; `definition.Instructions` içinde hazır gelir (deklaratif akışlarda `DeclarativeWorkflowBuilder.BuildInstructions`).
- `BuildToolsAsync<TSlots>(definition, ct)` (private): `Card`/`Account`/`Analyze` gruplarındaki tüm MCP araçlarını + `update_slot` + `propose_*` + `cancel_*` + `handoff_*` araçlarını birleştirir.
- `BuildUpdateSlotTool<TSlots>(description)` (private): DI'dan `IDeterministicActionExecutor<TSlots>`'u çözüp `BankValidatedSlotWriter<TSlots>` üzerinden StateBag'e **banka onaylıyorsa** yazan `update_slot` aracını üretir. Deklaratif akışların eksekütörü DI'da olmadığı için orada `null` döner ve doğrulama atlanır.
- `BuildProposeTool<TSlots>(proposeToolName)` (private): `session.ConstructSlots<TSlots>()` ile StateBag'i tipli nesneye çevirmeyi dener; eksik/geçersizse hatayı LLM'e döner, tamamsa `session.StoreProposedSlots` çağırıp `PROPOSAL_READY:` sinyaliyle JSON döner.
- `BuildCancelTool(cancelToolName)` (private): `CANCELLED` sinyalini döndüren iptal aracını üretir.
- `BuildHandoffTool(handoffToolName)` (private): `HANDOFF_TO_ORCHESTRATOR` sinyalini döndüren devretme aracını üretir — kullanıcının mesajı sorulan parametrenin cevabı değilse ajanın **değer uydurmak yerine** kullanabileceği tek çıkış budur.

**`WorkflowAgentDefinition`** (`Workflows/Engine/WorkflowAgentDefinition.cs`) — Bir akış ajanının kimliği. Alanları: `AgentName`, `Instructions`, `SlotUpdateDescription`, `ProposeToolName`, `CancelToolName`, `HandoffToolName` — hepsi **tek statik metottan** türetilir.
- `For(workflowName, instructions)` (static): Akış adından `{isim}Asistani`, `propose_{isim}`, `cancel_{isim}`, `handoff_{isim}` adlarının tümünü üretir (derlenmiş ve deklaratif akışlar arasında isimlendirme sapmasını imkânsız kılar).

**`WorkflowAgentSignals`** (`Workflows/Engine/WorkflowAgentSignals.cs`, `internal`) — Ajan↔motor arası sabit sinyal metinleri: `ProposalReady` (`"PROPOSAL_READY:"`), `Cancelled` (`"CANCELLED"`), `Handoff` (`"HANDOFF_TO_ORCHESTRATOR"`). Üçü de **araç sonucu** olarak döner; motor ajanın serbest metninde kelime aramaz.

### 5.4 Çekirdek Motor — `WorkflowEngine<TSlots>`

**`WorkflowEngine<TSlots>`** (`Workflows/Engine/WorkflowEngine.cs`) — **Tüm iş akışlarının tek motoru**; her HTTP isteğinde yeniden yaratılır, durumu `SessionContext`'te tutar. Bağımlılıkları: `SessionContext`, `AIAgent workflowAgent`, `IDeterministicActionExecutor<TSlots>`, `OrchestratorFallback`, `TraceHub`.

- `AdvanceAsync(userMessage, ct)`: **Tek genel giriş noktası.** `session.Phase`'e göre üç private metottan birine dallanır:

  **Evre 1 — Başlatma:**
  - `StartAsync(initialMessage, ct)`: `Phase = SlotFilling`'e çeker; StateBag özetini (`GetSlotStatus<TSlots>().Describe()`) hazırlar; kullanıcının ilk cümlesi varsa (zaten söylenmiş bilgiler tekrar sorulmasın diye) ajana kickoff mesajı olarak verir; `RunAgentTurnAsync` ile ajanı çalıştırıp `InterpretAgentOutputAsync`'e geçer.

  **Evre 2 — Parametre Toplama:**
  - `CollectSlotsAsync(userMessage, ct)`: Kullanıcının cevabını + güncel StateBag özetini ajana iletir, sonucu yine `InterpretAgentOutputAsync`'e verir.
  - `InterpretAgentOutputAsync(agentText, userMessage, ct)` (private): Sırayla kontrol eder — (1) `CANCELLED` sinyali → `session.CancelWorkflow()` ve akışı bitirir; (2) StateBag zaten tamamsa ve onaylanmış slot varsa → `ConcludeSlotFillingAsync`; (3) `PROPOSAL_READY:` sinyali + geçerli JSON → slotları kaydedip `ConcludeSlotFillingAsync`; (4) `HANDOFF_TO_ORCHESTRATOR` sinyali → `orchestratorFallback` ile soruyu orkestratöre devreder, akış bozulmaz; (5) hiçbiri değilse ajanın sorusunu kullanıcıya iletir.

  **Evre 3 — Onay (HITL):**
  - `ConcludeSlotFillingAsync(slots, ct)` (private): `actionExecutor.BuildConfirmationView(slots)` ile onay ekranını üretir; `session.StoreProposedSlots`, `PendingConfirmation`, `IdempotencyToken` (SHA-256), `CompletedOutcome = null` ayarlar. Ardından yol ikiye ayrılır: **`action.CallsBank` false ise** (YAML'da `action.path` yok) onay kapısı kurulmaz, `RunActionGraphAsync` doğrudan `isApproved: true` ile çalışır ve akış aynı istekte biter — bankaya istek gitmeyen bir akışta onaylanacak bir işlem yoktur ve kullanıcıyı ödeme onayı ekranına götürmek gerçekleşmeyecek bir işlemi gerçekleşecekmiş gibi gösterir. **True ise** `Phase = AwaitingApproval` yazılır ve **MAF grafiği burada henüz başlatılmaz.**
  - `ApplyApprovalDecisionAsync(userMessage, ct)`: `ApprovalIntentParser.Parse` ile niyeti okur; `Unclear` ise onay saymaz ve tekrar sorar; onay bekleyen veri yoksa (TTL) oturumu iptal edip dürüst mesaj döner; **aynı idempotency token ile ikinci onay** gelmişse eylemi tekrar çalıştırmadan önceki `CompletedOutcome`'u döner; aksi hâlde `RunActionGraphAsync`'i çalıştırıp sonucu `session.CompletedOutcome`'a yazar.
  - `RunActionGraphAsync(slots, confirmationView, isApproved, ct)` (private): Onay kapısının kurulup kurulmayacağını `action.CallsBank` belirler ve `DeterministicActionWorkflow.Build`'e `requiresApproval` olarak geçer; grafiğin girdisi kapılı yolda onay ekranı, kapısız yolda doğrudan onay kararıdır (kapısız grafikte `RequestInfoEvent` hiç doğmaz); `InProcessExecution.RunStreamingAsync` ile **aynı HTTP isteği içinde** çalıştırır; `RequestPortEvent` geldiğinde onay kararını anında `SendResponseAsync` ile porta verir; `WorkflowOutputEvent`'ten `WorkflowOutcome`'u yakalar; `ExecutorFailedEvent`/`WorkflowErrorEvent` durumunda sebebi yutmadan `WorkflowOutcome.Failure` üretir.
  - `FinishWorkflow(outcome)` (private): Başarılıysa `session.CompleteWorkflow()`, değilse `session.CancelWorkflow()` çağırıp uygun `WorkflowStepResult`'ı döner.

  **Ortak yardımcılar:**
  - `RunAgentTurnAsync(message, ct)` (private): `ResumeOrCreateAgentSessionAsync` ile konuşmayı geri yükler, `workflowAgent.RunAsync` çağırır, geçmişi tekrar `session.WorkflowConversation`'a serileştirir (`Clone()` ile — `JsonDocument` ömrü scope'u aşacağı için bağımsız kopya alınır).
  - `ResumeOrCreateAgentSessionAsync(ct)` (private): Serileştirilmiş geçmiş varsa geri yükler, yoksa/bozuksa yeni oturum açar.
  - `Contains(agentText, marker)` / `TryParseProposal(agentText)` (private, static): Sinyal arama ve `PROPOSAL_READY:` sonrası JSON'ı `TSlots`'a deserialize etme.
  - `ComputeIdempotencyToken(confirmationView)` (private, static): Akış adı + sıralı parametrelerden SHA-256 özet üretir — aynı teklif için ikinci onayı tanımanın anahtarıdır.

### 5.5 StateBag ↔ Tipli Nesne Dönüşümü — `Slots/`

**`SlotBinder`** (`Workflows/Slots/SlotBinder.cs`, `internal static`) — StateBag (ham sözlük) ile tipli `TSlots` record'ları arasındaki **saf** dönüşüm katmanı; hiçbir bağımlılığı/yan etkisi yoktur.
- `IsDictionarySlotType<TSlots>()`: Slot tipi bir sözlük mü (deklaratif akış) yoksa record mü?
- `ApplyDateDefaults<TSlots>(slotValues)`: `DateOnly` tipli alanları `BankClock.Today`'den otomatik doldurur (kullanıcıya sorulmaz).
- `GetStatus<TSlots>(slotValues, schemaTemplate)`: Her slot adı için değer var/anlamlı mı diye bakıp `SlotStatus` (dolu/eksik) üretir.
- `Construct<TSlots>(slotValues, onInvalidSlot)`: En çok parametre alan constructor'ı bulup StateBag değerleriyle doldurmayı dener; dönüşemeyen alanı `onInvalidSlot` ile bildirir (sessizce varsayılana düşürmez).
- `Flatten<TSlots>(slots)`: Kurulmuş bir slot nesnesini tekrar düz anahtar/değer çiftlerine açar.
- `ResolveSlotNames<TSlots>(slotValues, schemaTemplate)` (private): Beklenen slot adlarını (record için property adları, sözlük için toplanan+şema anahtarları) çözer.
- `IsBlank(value)` (private, static): Değer var ama anlamsız mı (boş metin, sıfır/negatif tutar) kontrolü.
- `DatePropertiesOf` / `PublicPropertiesOf` (private, static): Reflection sonuçlarını `ConcurrentDictionary` ile önbellekler.

**`SlotStatus`** (record, `Slots/SlotBinder.cs` içinde) — Alanları: `FilledSlots` (dolu), `MissingSlots` (eksik). `IsComplete` ve `Describe()` (LLM'e verilecek tek satırlık özet) sağlar.

**`SlotValueConverter`** (`Workflows/Slots/SlotValueConverter.cs`, `internal static`) — Ham değeri hedef .NET tipine **sıfır hata toleransıyla** çevirir (başarısızlık sessizce 0/null'a düşürülmez).
- `TryConvert(rawValue, targetType, out converted)`: `JsonElement` unwrap eder, tipi zaten uyuyorsa direkt kabul eder, aksi hâlde `string/decimal/int/bool/DateOnly` için özel dönüştürücülere yönlendirir.
- `TryUnwrapJson` / `TrySetString` / `TryConvertDecimal` / `TryConvertInt32` / `TryConvertBoolean` / `TryConvertDateOnly` (private): Her biri tek bir hedef tip için dönüşüm dener; `TryConvertDecimal` hem Türkçe (`12.500,75`) hem invariant (`12500.75`) biçimi ve `₺`/`TL` sembollerini kabul eder.

**`SlotConstructionResult<TSlots>`** (record, `Slots/SlotConstructionResult.cs`) — Alanları: `Slots` (kurulan nesne ya da `null`), `InvalidSlots`, `MissingSlots`. `IsComplete` ve `DescribeProblems()` (LLM'in kullanıcıya tekrar sorabileceği özet) sağlar.

### 5.6 Banka Ön-Doğrulaması — `Validation/`

Doğrulama **kuralları** `Banking.Domain` servislerindedir; bu klasörde yalnızca iki dosya vardır ve ikisi de kural değil, **mekanizma** taşır. Kısmen dolu slotları bankanın imzasına eşleyen adaptör kodu ise akışın kendi eksekütöründedir (`IDeterministicActionExecutor<TSlots>.ValidateFilledSlotsAsync`) — akış başına ayrı bir doğrulayıcı arayüzü/sınıfı yoktur.

**`BankValidatedSlotWriter<TSlots>`** (`Validation/BankValidatedSlotWriter.cs`) — `update_slot` aracının arkasındaki **tek geçiş noktası**; bağımlılıkları `ISessionAccessor`, `IDeterministicActionExecutor<TSlots>?`, `TraceHub`.
- `WriteSlotIfBankAcceptsAsync(slotName, candidateSlotValue, ct)`: Eksekütör yoksa (deklaratif akış) direkt StateBag'e yazar; varsa aday değeri mevcut dolu değerlerle birleştirip bankaya sorar — **kabul edilirse** StateBag'e yazar, **reddedilirse hiç yazmaz** ve bankanın gerekçesini LLM'e döner.
- `BuildSlotValuesIncludingCandidate(session, slotName, candidateSlotValue)` (private, static): Mevcut dolu StateBag değerlerinin üzerine aday değeri koyan geçici (yazılmamış) kopya oluşturur.
- `BuildAcceptedMessage` / `BuildRejectedMessage` (private, static): LLM'e dönecek araç sonucu metnini üretir; ret mesajına varsa kabul edilebilir aralık ipuçlarını (`BankValidationResult.Hints`) ekler.

**`PartiallyFilledSlotReader`** (`Validation/PartiallyFilledSlotReader.cs`, static) — Kısmen dolu sözlükten **"değer yok" ile "değer sıfır"u kesinlikle ayırarak** okur.
- `ReadOptionalText(filledSlotValues, slotName)` / `ReadOptionalAmount(...)`: Sırasıyla `string?`/`decimal?` okur.
- `TryRead(filledSlotValues, slotName, targetType, out value)` (private): Anahtar yok/`null`/dönüşüm başarısız → hepsi aynı anlama gelir: "banka bu alanı bilmiyor" (`false`).

### 5.7 Onay Kapısı (HITL) — `Approval/`

**`DeterministicActionWorkflow`** (`Workflows/Approval/DeterministicActionWorkflow.cs`, static) — İnsan onayı gerektiren eylemin MAF grafiğini kurar.
- `Build<TSlots>(workflowName, slots, actionExecutor, hub, requiresApproval)`: `requiresApproval=false` ise doğrudan eksekütör düğümünü çalıştıran tek düğümlü grafik kurar; `true` ise `RequestPort.Create<GenericConfirmationView, bool>()` (insan onayı) → eksekütör düğümü kenarlı iki düğümlü grafik kurar.

**`DeterministicActionStep<TSlots>`** (aynı dosyada, `internal`, `Executor<bool, WorkflowOutcome>`) — Onay kararını alıp eylemi LLM müdahalesi olmadan çalıştıran MAF adımı.
- `HandleAsync(approved, context, ct)`: `actionExecutor.ExecuteAsync(slots, approved, ct)`'i çağırır ve sonucu döner.

**`ApprovalIntentParser`** (`Workflows/Approval/ApprovalIntentParser.cs`, static partial) — Onay cevabının niyetini yorumlar; **önce olumsuzluk sonra olumluluk** aranır (Türkçede olumsuzluk fiilin içinde olduğu için).
- `Parse(answer)`: Boşsa/kısayol değilse `RejectionPattern`/`ApprovalPattern` regex'lerine bakar; ikisi de eşleşmezse `Unclear` döner (sessizce onaya düşülmez).
- `RejectionPattern()` / `ApprovalPattern()` (generated regex, private): Türkçe fiil çekimlerini kapsayan derlenmiş desenler.

### 5.8 Deterministik Eylemler — `Actions/`

**`IDeterministicActionExecutor<TSlots>`** (`Workflows/Contracts/IDeterministicActionExecutor.cs`) — Bir akışın **banka tarafındaki tüm temas noktalarını** taşıyan tek sözleşme.
- `ValidateFilledSlotsAsync(filledSlotValues, ct)`: Kısmen dolu slotlarla bankanın ön-doğrulamasını çağırır (slotlar dolarken, her `update_slot`'ta). Kural içermez, yalnızca isim eşler.
- `BuildConfirmationView(slots)`: Onay ekranı görünümünü üretir.
- `ExecuteAsync(slots, approved, ct)`: Kararı uygular; **hiçbir koşulda exception sızdırmaz**, tüm başarısızlıklar `WorkflowOutcome.Failure` olarak döner.

**`PaymentActionExecutor`** (`Workflows/Actions/PaymentActionExecutor.cs`) — `IDeterministicActionExecutor<PaymentSlots>`; bağımlılıkları `ICollectionService`, `ICardService`, `IAccountService`, `TraceHub`.
- `ValidateFilledSlotsAsync(...)`: `PartiallyFilledSlotReader` ile okuyup `ICollectionService.ValidateCardPaymentBeforeExecution`'a delege eder.
- `BuildConfirmationView(slots)`: `GenericConfirmationView.FromSlots` + kart/hesap maskeleme bilgilerini ekler.
- `ExecuteAsync(slots, approved, ct)`: Onaylanmadıysa `RejectedByUser` döner; onaylandıysa `ICollectionService.Charge` çağırır, başarıda makbuz bilgisiyle `Success`, `InvalidOperationException` yakalanırsa gerçek sebeple `Failure` döner.

**`TransferActionExecutor`** (`Workflows/Actions/TransferActionExecutor.cs`) — `IDeterministicActionExecutor<TransferSlots>`; aynı desende `ITransferService.ValidateMoneyTransferBeforeExecution` ve `Transfer` çağırır.

**`DeclarativeActionExecutor`** (`Workflows/Actions/DeclarativeActionExecutor.cs`) — `IDeterministicActionExecutor<Dictionary<string,object?>>`; deklaratif (YAML/JSON) akışlar içindir.
- `ValidateFilledSlotsAsync(...)`: Her zaman `Acceptable` döner — doğrulanacak bir banka aksiyonu yoktur.
- `BuildConfirmationView(slots)`: Sözlüğü doğrudan `GenericConfirmationView`'a sarar.
- `ExecuteAsync(slots, approved, ct)`: **Gerçek bir banka aksiyonu tetiklemez** — yalnızca "talep kaydedildi" sonucu üretir (makbuz üretmez); YAML'daki `action:` bloğunun gerçek MCP aracına bağlanması ayrı bir iş kalemidir. **Bu yüzden `card-payment`/`money-transfer` gibi para taşıyan akışlar derlenmiş eksekütör olmak zorundadır** — `WorkflowRegistry` ad çakışmasını bilerek engeller.

### 5.9 Deklaratif (No-Recompile) Akışlar — `Declarative/`

**`DeclarativeWorkflowBuilder`** (`Workflows/Declarative/DeclarativeWorkflowBuilder.cs`, static) — MAF YAML/JSON şemasını çalışma zamanında ayrıştırır.
- `ParseFromYaml(yamlContent)` / `ParseFromJson(jsonContent)`: `YamlDotNet`/`System.Text.Json` ile `MicrosoftAgentDeclarativeDefinition`'a deserialize eder.
- `BuildInstructions(definition, agentDefinition)`: Tanımdan (`<identity>`, `<custom_instructions>`, `<required_parameters>` blokları) LLM talimatı üretir; kural, sunum ve koruma bloklarını `AgentPrompts.BuildSlotFillingRules`'tan alır ve araç adlarını `agentDefinition`'dan **okur** (yeniden türetmez). Dahili kimlik guardrail'ini ayrıca eklemez: kural zaten koruma bloğundadır, iki kez basılması hem token yakıyor hem de YAML'da `instructions` yoksa kuralın bir kopyasının hiç eklenmemesi gibi tutarsız bir davranış üretiyordu.

**`MicrosoftAgentDeclarativeDefinition`** (`Workflows/Declarative/MicrosoftAgentDeclarativeDefinition.cs`) — Alanları: `Kind`, `Name`, `DisplayName`, `Description`, `Instructions`, `MkCode`, `Model` (`DeclarativeModelConfig`), `OutputSchema`/`InputSchema` (`DeclarativeSchemaConfig` → `DeclarativePropertyConfig` sözlüğü: `Type`, `Required`, `Description`).

**Kayıt akışı** (`POST /api/workflows/register-declarative`, `Program.cs`): İçerik YAML/JSON olarak ayrıştırılır → `name` dosya-yolu-güvenliği için harf/rakam/`-`/`_` ile sınırlanır → `WorkflowRegistry.RegisterDeclarativeWorkflow` (derlenmiş adla çakışmaya karşı korumalı) → `declarative-agents/{ad}.yaml` diske yazılır → `SemanticWorkflowSearcher.AddOrUpdate` ile **anında** RAG indeksine eklenir → `TraceHub`'a yayınlanır.

### 5.10 Ortak Sözleşme Tipleri — `Contracts/`

**`IWorkflowEngine`** (`Workflows/Contracts/IWorkflowEngine.cs`) — Tek metotlu sözleşme: `AdvanceAsync(userMessage, ct)`.

**`WorkflowStepResult`** (record, `Contracts/WorkflowStepResult.cs`) — Alanları: `SessionId`, `Status` (`WorkflowStatuses`), `Prompt?`, `Confirmation?` (`GenericConfirmationView`), `Outcome?` (`WorkflowOutcome`). Fabrika metotları: `Ask`, `AwaitApproval`, `Completed`, `Ended`.

**`WorkflowOutcome`** (record, `Contracts/WorkflowOutcome.cs`) — Alanları: `WorkflowName`, `Status` (`Succeeded`/`RejectedByUser`/`Failed`), `UserMessage` (başarısızlıkta **gerçek sebep**), `ReceiptId?`, `Details` (sözlük). Fabrika metotları: `Success`, `RejectedByUser`, `Failure`. Bu tip sayesinde gateway/konuşma katmanları akışa özel sonuç tipi tanımak zorunda kalmaz.

**`GenericConfirmationView`** (record, `Contracts/GenericConfirmationView.cs`) — Alanları: `WorkflowName`, `Parameters` (sözlük), `ScreenMkCode?`.
- `FromSlots<TSlots>(slots)` (static): Herhangi bir `TSlots` nesnesini (record ya da sözlük) reflection ile `GenericConfirmationView`'a çevirir; `[Workflow(..., MkCode=...)]`'dan ekran kodunu okur.
- `ReadDictionary` / `ReadProperties` (private, static): Sözlük ya da public property'lerden parametre haritası çıkarır.

**`IDeterministicActionExecutor<TSlots>`**: Bkz. Bölüm 5.8.

---

## 6. Çıkış — Yanıtın UI'a Dönüşü

`FlowRouter`'dan dönen `FlowExecutionResult`, `GatewayService.HandleInSessionAsync` içinde şu sırayla işlenir:

**`GatewayResponseMapper`** (`src/Banking.Gateway/GatewayResponseMapper.cs`) — Bağımlılıkları `TraceHub`, `ILogger`. **Bankacılık metni üretmez**, akışın kendi ürettiği metni taşır.
- `Map(flowResult)`: `ExecutionType`'a göre `MapWorkflowStep` ya da `MapOrchestratorReply`'a dallanır.
- `MapWorkflowStep(stepResult)` (private): `Question` → `TEXT`; `Confirmation` → `BuildConfirmationScreen` ile `CONFIRM_PAYMENT`; `Completed` (başarılı) → `TEXT` (outcome mesajı); `Cancelled` → `TEXT`.
- `MapOrchestratorReply(reply)` (private): Tetiklenen araç `ekran_ac` ise `OPEN_SCREEN`, değilse düz `TEXT` döner.
- `BuildConfirmationScreen(confirmation)` (private): `confirmation.ScreenMkCode`'u `MkCodesRegistry.Get` ile çözer; kod tanımlı değilse ya da registry'de yoksa **tahmin yapmaz**, akış adını/ham kodu olduğu gibi iletip uyarı loglar.

**`GatewayResponse`** (record, `Gateway/Models/GatewayResponse.cs`) — Java MW'nin beklediği düz JSON şeması. Sabit alanlar: `Type` (`GatewayResponseType`: `TEXT`/`OPEN_SCREEN`/`CONFIRM_PAYMENT`), `Text?`, `MkCodeValue?`, `SessionToken?`; `AdditionalProperties` (`[JsonExtensionData]`) diğer tüm alanları JSON kökünde düzleştirir. Fabrika metotları: `AsText`, `AsOpenScreen`, `AsGenericConfirmation`.

**`GatewayRequest`** (record, `Gateway/Models/GatewayRequest.cs`) — Gelen istek şeması: `Text` (required), `ThemeValue?`, `SessionToken?`.

Ardından `GatewayService.FinalizeResponse`:

**`ResponseSanitizer`** (`src/Banking.Gateway/ResponseSanitizer.cs`) — **6 katmanlı deterministik çıktı güvenlik bariyeri**; LLM'in prompt'u takip edip etmediğinden bağımsız çalışan son savunma hattı (Defense-in-Depth Katman 3). Bağımlılıkları: `ILogger`, `TraceHub`, `IToolRegistry`.
- `Sanitize(text)`: Sırasıyla `StripXmlBlocks` → `StripXmlTags` → `StripToolNames` → `StripThinkingPatterns` → `StripSystemPromptLeaks` → `StripEndpoints` → `MaskSessionTokens` → `NormalizeWhitespace` uygular; metin tamamen boşalırsa sabit `FallbackMessage` döner.
- `StripXmlBlocks(text, ref violations)` / `StripXmlTags(...)`: Dahili XML etiketlerini ve aralarındaki içeriği regex ile temizler.
- `StripToolNames(text, ref violations)`: `IToolRegistry.GetAllSanitizableToolNames()`'ten türetilen dinamik regex ile araç adı geçen **satırları** tamamen kaldırır.
- `StripThinkingPatterns(text, ref violations)`: "araç çağırıyorum", "uzmana danışıyorum" gibi düşünce/planlama ifadesi içeren satırları kaldırır.
- `StripSystemPromptLeaks(text, ref violations)`: Sistem prompt'undan sızmış tam ifade kalıplarını kaldırır.
- `StripEndpoints(text, ref violations)`: `localhost`, `/mcp`, `/a2a/...` gibi dahili adresleri kaldırır.
- `MaskSessionTokens(text, ref violations)`: `gw-`/`cs-` token'larını **satırı silmeden** `[GİZLİ]` ile maskeler.
- `ApplyRegex` / `StripSentencesContaining` (private): Sırasıyla eşleşmeyi doğrudan metinden çıkarma ve eşleşen satırı bütünüyle atma yardımcıları.
- `LogViolation(category, snippet)` (private): Her ihlali hem `ILogger.LogWarning` hem `TraceHub.Publish("SECURITY", ...)` ile çift kanaldan raporlar.
- `NormalizeWhitespace(text)` (private, static): Ardışık boş satırları teke indirir.
- `BuildToolNameRegex(internalToolNames)` (private, static): Tüm dahili araç adlarını kelime-sınırlı, tırnak-toleranslı tek bir regex'te birleştirir.

Sanitize edilmiş yanıt, `GatewayResponse` olarak HTTP 200 ile UI'a döner; `RunInSessionScopeAsync`'in `finally` bloğu bu sırada (yanıt oluşturulduktan hemen sonra, controller dönmeden önce) `SessionState`'i **bir kez** `ISessionStore.SaveAsync` ile yazıp kilidi bırakır — böylece bir sonraki istek scope'u açabilir.

---

## 7. MCP Sunucu Katmanı — `Banking.Mcp`

Uzman ajanların ve iş akışı ajanlarının araç çağrıları nihayetinde buraya, süreç-içi loopback MCP server'a düşer.

### 7.1 Araç Kayıt Sistemi

**`IToolRegistry`** (`Registry/IToolRegistry.cs`) — Sözleşme: `Get`, `GetNamesByGroup`, `GetAllSanitizableToolNames`, `ScanAssembly`, `ValidateDiscoveredMcpTools`.

**`ToolRegistry`** (`Registry/ToolRegistry.cs`) — Lazy singleton (`Instance`); ilk erişimde kendi assembly'sini tarar ve çekirdek olmayan-MCP araçlarını kaydeder.
- `ScanAssembly(assembly)`: `[McpServerToolType]` veya `Tools` son ekli sınıfları bulur, her `[McpServerTool]` metodunu `ToolDefinition` olarak kaydeder (grup, `[ToolGroup]`'tan ya da sınıf adından tahmin edilir).
- `Register(definition)`: Tanımı sözlüğe ekler (`TryAdd`).
- `Get(name)`: Tanımı döner; yoksa `KeyNotFoundException`.
- `GetNamesByGroup(group)`: Gruba göre araç adlarını filtreler.
- `GetAllSanitizableToolNames()`: Kayıtlı tüm araç adlarını döner (`ResponseSanitizer` bunu kullanır) — sistemdeki her araç dahilidir, hiçbiri kullanıcı yanıtında görünmemelidir.
- `ValidateDiscoveredMcpTools(discoveredToolNames)`: Registry'deki MCP araçlarıyla canlı server'ın araç listesini karşılaştırır; eksik varsa **fail-fast** `InvalidOperationException` fırlatır.
- `RegisterNonMcpCoreTools()` (private): Köprü araçlarını (`hesap_uzmanina_sor` vb.), `start_workflow`, `ekran_kodu_ara`, `ekran_ac`'ı elle kaydeder (bunlar MCP değildir, orkestratör seviyesindedir).
- `InferGroupFromTypeName(typeName)` (private, static): `[ToolGroup]` yoksa sınıf adının önekinden (`Account`/`Card`/`Analyze`) grup tahmini yapar.

**`ToolGroupAttribute`** (`Registry/ToolGroupAttribute.cs`) — Sınıf/metodu bir `ToolGroup`'a etiketleyen özel attribute.

**`ToolDefinition`** (record, `Registry/IToolRegistry.cs` içinde) — Alanları: `Name`, `Group` (`ToolGroup`: `Account/Card/Analyze/Orchestrator`), `Type` (`ToolType`: `Mcp/OrchestratorBridge/Workflow`), `Description`.

### 7.2 Araç Sınıfları

**`AccountsTools`** (`AccountsTools.cs`, `[ToolGroup(Account)]`) — Bağımlılıkları `IAccountService`, `CustomerContext`.
- `ListAccounts()`: Müşterinin tüm hesaplarını (IBAN, tür, bakiye) listeler.
- `GetAccountBalance(accountId)`: Belirli hesabın güncel bakiyesini döner; hesap müşteriye ait değilse hata nesnesi döner.
- `GetTransactions(accountId, fromDate?, toDate?)`: Hesap hareketlerini (opsiyonel tarih filtresiyle) döner.

**`CardsTools`** (`CardsTools.cs`, `[ToolGroup(Card)]`) — Bağımlılıkları `ICardService`, `CustomerContext`.
- `ListCards()`: Tüm kredi kartlarını limit/borç özetiyle listeler.
- `GetCardDebt(cardId)`: Sadece güncel borç ve asgari ödemeyi döner.
- `GetAvailableLimit(cardId)`: Kullanılabilir limiti döner.
- `GetCardDetails(cardId)`: Limit, borç, ekstre/son ödeme tarihi dahil tüm detayları döner.

**`AnalyzeTools`** (`AnalyzeTools.cs`, `[ToolGroup(Analyze)]`) — Bağımlılıkları `IAnalyzeService`, `SemanticCampaignSearcher`, `SemanticFinancingSearcher`, `ICampaignCatalogService`, `IFinancingCatalogService`.
- `GetCreditCardStatement(filePath?)`: Ham ekstre verisini JSON olarak döner.
- `AnalyzeStatementSummary(filePath?)`: Genel dönem özetini (`IAnalyzeService.AnalyzeStatement`) döner.
- `AnalyzeCategoryBreakdown(filePath?)`: Sektör/kategori bazlı harcama dağılımını döner.
- `AnalyzePaymentTypeRatio(filePath?)`: Peşin/taksitli harcama oranını döner.
- `AnalyzeBonusAndPoints(filePath?)`: Puan/bonus kazanımlarını analiz eder.
- `AnalyzeTopMerchants(filePath?, topCount)`: En çok harcama yapılan işyerlerini sıralar.
- `CompareSpendingPeriods(...)`: İki dönemi (opsiyonel kategori filtresiyle) karşılaştırır.
- `KampanyaVektorelAra(sorguVektoruMetni, mccKategorisi?, topK)`: `SemanticCampaignSearcher.Search` ile profile uygun kampanyaları döner.
- `GetCampaings(campaignId?)`: `ICampaignCatalogService` üzerinden tüm/kimlikli kampanyayı döner.
- `RecommendFinancing(query, topK)`: Harcama profilinden zenginleştirilmiş sorguyla `SemanticFinancingSearcher.Search` çağırıp kişiye özel finansman önerir.
- `GetFinancingOptions(type?)`: `IFinancingCatalogService` üzerinden ham finansman kataloğunu döner.
- `GetTransactions(filePath)` / `ResolvePath(filePath)` (private): Ortak dosya okuma yardımcıları.

**Sabitler:** `AccountToolNames`, `CardToolNames`, `AnalyzeToolNames` (`Constants/*.cs`) — her aracın MCP adını `const string` olarak tutar (tip güvenliği).

---

## 8. Banking.Domain — İş Kuralları ve Veri

Tüm servisler `InMemoryBankStore` (tekil, thread-safe `ConcurrentDictionary` + `lock`) üzerinde çalışır; hiçbiri başka bir projeye bağımlı değildir.

**`InMemoryBankStore`** (`Data/InMemoryBankStore.cs`) — Demo müşterisi `CUST-001` için hesap/kart/kampanya/finansman/hareket verisini bellekte tutar.
- `GetCustomer/GetAccount/GetCard/GetCampaign(id)`: Kimliğe göre tekil kayıt getirir.
- `AccountsOf/CardsOf(customerId)`, `TransactionsOf(accountId)`: Filtrelenmiş koleksiyonlar döner.
- `ApplyPayment(cardId, sourceAccountId, amount)`: **Atomik** (lock altında) kart borcu azaltma + hesap bakiyesi düşürme; bakiye/borç/tutar kurallarını ihlalde `InvalidOperationException` fırlatır.
- `ApplyTransfer(sourceAccountId, targetIban, amount, description)`: Kaynak hesaptan düşer, hedef IBAN bankanın kendi hesaplarından biriyse (Havale) karşı hesaba da işler; hareket kayıtlarını (`Giden`/`Gelen`) ekler.
- `Seed()` (private): Demo müşteri, 2 hesap, 2 kart, 4 kampanya, 7 finansman seçeneği ve 5 hareketi sabit verilerle doldurur.

**`AccountService`** (`IAccountService`) — `GetAccounts`, `GetAccount`, `GetTransactions` — `InMemoryBankStore`'a ince bir okuma katmanı.

**`CardService`** (`ICardService`) — `GetCards`, `GetCard`. Borç/limit gibi türetilmiş değerler `Card` modelinin kendi property'lerinden okunur; servis üzerinde ayrı erişimci tutulmaz.

**`CollectionService`** (`ICollectionService`) — Tahsilat/ödeme iş kuralları.
- `ValidateCardPaymentBeforeExecution(cardId?, paymentAmount?, sourceAccountId?)`: **Dry-run** — kart/hesap bulunabilirliği, borç sıfır mı, tutar pozitif mi, tutar borcu/bakiyeyi aşıyor mu kontrollerini elindeki bilgiyle sırayla yapar; her ret bir `BankRejectionCodes` kodu + kullanıcıya gösterilecek mesaj + (varsa) kabul edilebilir aralık ipucu döner.
- `Charge(cardId, amount, sourceAccountId, date)`: **Geri alınamaz.** Önce aynı doğrulamayı (`ValidateCardPaymentBeforeExecution`) tekrar çalıştırır (kural çiftlenmesi yok), sonra `InMemoryBankStore.ApplyPayment`'ı çağırıp makbuz üretir.
- `BuildAcceptableHints(card, sourceAccount)` (private, static): Kabul durumunda dahi "azami kabul edilebilir tutar" gibi ipuçlarını üretir.

**`TransferService`** (`ITransferService`) — Aynı desende transfer kuralları.
- `ValidateMoneyTransferBeforeExecution(sourceAccountId?, targetIban?, transferAmount?)`: Hesap bulunabilirliği, IBAN geçerliliği (`TurkishIbanValidator`), tutar pozitifliği/bakiye yeterliliği, hedef=kaynak kontrolünü yapar.
- `Transfer(sourceAccountId, targetIban, amount, description)`: Doğrulamayı tekrar çalıştırıp `InMemoryBankStore.ApplyTransfer`'ı çağırır, makbuz üretir.
- `ValidateTargetIban(targetIban, sourceAccount)` (private, static): IBAN format/checksum/kendi-kendine-transfer kontrolünü `TurkishIbanValidator` ile yapar.

**`AnalyzeService`** (`IAnalyzeService`) — Kredi kartı ekstresi üzerinde saf hesaplama servisi.
- `LoadTransactionsFromJson(jsonContentOrPath)`: JSON metni ya da birkaç aday yoldan dosya okuyup iç içe geçmiş şemadan (`GetCreditCardCycleInfoForAllCardsResult.Result.MainCardTransaction[].CardTransactionList[]`) işlem listesini çıkarır; bulunamazsa boş liste döner (çökmez).
- `AnalyzeStatement(transactions)`: Toplam harcama + kategori + ödeme tipi + bonus + top-merchant özetini tek nesnede birleştirir.
- `AnalyzeCategories(transactions)`: MCC grubuna göre tutar/adet/yüzde dağılımı.
- `AnalyzePaymentTypes(transactions)`: Taksitli/peşin ayrımı ve oranları.
- `AnalyzeBonusAndPoints(transactions)`: Kampanya puanı ile standart puanı ayırıp verimlilik yüzdesini hesaplar.
- `AnalyzeTopMerchants(transactions, topCount)`: İşyeri adına göre gruplanmış en yüksek harcamalar.
- `ComparePeriods(transactions, currentStart, currentEnd, previousStart, previousEnd, mccGroupType?)`: İki dönem arasındaki tutar farkı ve yüzde değişimi.
- `MapToCardTransactionItem(jsonElement)` / `CalculatePercentage` / `GetStringOrDefault` / `CleanMerchantName` (private, static): JSON eşleme ve biçimlendirme yardımcıları.

**`CampaignCatalogService`** (`ICampaignCatalogService`) — `GetActiveCampaigns`, `FindCampaignById` — kampanya okuma tek kapısı (MCP araçları `InMemoryBankStore`'a asla doğrudan dokunmaz).

**`FinancingCatalogService`** (`IFinancingCatalogService`) — `GetAllFinancingOptions`, `FindFinancingOptionsByType`.

**`CustomerContext`** — `CurrentCustomerId` (demo'da sabit `CUST-001`; gerçek kimlik doğrulamaya kolayca bağlanabilir tek nokta).

**`BankValidationResult`** (record, `Validation/BankValidationResult.cs`) — Bankanın ön-doğrulama cevabı; **bankanın sözleşmesidir, ajan katmanının değil**. Alanları: `IsAcceptable`, `RejectionCode?` (`BankRejectionCodes`), `UserFacingMessage?`, `Hints` (`BankValidationHintKeys`: azami tutar, kullanılabilir bakiye, güncel borç). Fabrika metotları: `Acceptable`, `Rejected`.

**`TurkishIbanValidator`** (static, `Validation/TurkishIbanValidator.cs`) — ISO 13616 / mod-97 doğrulaması; bilinçli olarak Domain katmanında durur.
- `Validate(iban)`: Yapı (`TR` + 26 karakter + rakam) ve mod-97 kontrol basamağını sırayla kontrol edip `Valid/MalformedStructure/ChecksumMismatch` döner.
- `Normalize(iban)`: Boşluksuz, büyük harfli kanonik forma çevirir.
- `HasValidStructure` / `CalculateMod97Remainder` (private, static): Yapı kontrolü ve taşmasız (`long`'a sığdırmadan basamak basamak) mod-97 hesaplaması.

### 8.1 Slot Record'ları (Attribute-Driven Discovery)

**`WorkflowAttribute`** (`Attributes/WorkflowAttribute.cs`) — `[Workflow("ad", MkCode = "...")]`; `WorkflowRegistry.AutoDiscoverWorkflows` bunu tarar.

**`PaymentSlots`** (`Records/PaymentSlots.cs`) — `[Workflow("card-payment", MkCode="CreditCardPayment")]`; alanları `CardId`, `Amount`, `SourceAccountId`, `Date` (her biri `[Description]` ile LLM'e açıklanır). `PaymentSlotKeys` sabit anahtarları taşır.

**`TransferSlots`** (`Records/TransferSlots.cs`) — `[Workflow("money-transfer", MkCode="TransferMenu")]`; alanları `TargetIban`, `Amount`, `SourceAccountId`, `Description`. `TransferSlotKeys` sabit anahtarları taşır.

**`BankClock`** — `Today` sabit `2026-06-30` (demo için deterministik "bugün").

### 8.2 Semantik Arama Motorları (ONNX / Yerel Embedding)

Dördü de aynı desende çalışır: `Initialize()` ile kataloğu vektörize edip belleğe alır, `Search(intent, ...)` ile kosinüs benzerliğine göre en iyi eşleşmeleri (bir baraj üstünde) döner. Hiçbiri dış ağa çıkmaz (`SmartComponents.LocalEmbeddings`).

**`SemanticMkCodeSearcher`** (`MkCodes/`) — Ekran kodu (mkCode) araması; baraj **0.70**. `MkCodesRegistry` (`Get`, `GetAll`) statik olarak varsayılan kodları + (varsa) `xmlviewer (1).xml`/`mkcodes.json` dosyasından yüklenenleri tutar. `MkCodeDefinition`: `Value`, `Label`.

**`SemanticCampaignSearcher`** (`Campaigns/`) — Kampanya araması; baraj **0.70**; opsiyonel MCC filtresi uygular.

**`SemanticFinancingSearcher`** (`Financing/`) — Finansman önerisi araması; baraj **0.65**.

**`SemanticWorkflowSearcher`** (`Workflows/SemanticWorkflowSearcher.cs`) — İş akışı yönlendirme araması; baraj **0.5** (adaylık için — asıl otomatik başlatma eşiği `AgentRuntime`'daki **0.65**'tir).
- `Initialize(discoveredWorkflows)`: Başlangıçta keşfedilen tüm akışları indeksler.
- `AddOrUpdate(item)`: Çalışma zamanında (deklaratif akış kaydında) tek bir akışı anında vektörize edip indekse ekler/günceller (**sıfır cold-start**).
- `Search(intent, topK)`: En yakın akışları skorlarıyla döner.

**`WorkflowMetadata`** (record) — `WorkflowName`, `Description`, `SlotType`.

### 8.3 Sabitler

- **`AgentConstants`**: `SpecialistNames` (`HesapUzmani`/`KrediKartiUzmani`/`AnalizUzmani`), `ToolGroups`, `WorkflowNames`.
- **`ToolNames`**: `StartWorkflow`, `OpenScreen`, `SearchScreenCode`, `OpenScreenTargetParameter` (araç parametre adı ile gateway okuma anahtarının **aynı** olduğunu garanti eden tek sabit), köprü araç adları.
- **`TraceCategories`**: `Gateway/FlowRouter/System/Workflow/Security/Service/Llm/Context/Mcp/A2a`.
- **`WorkflowPhases`**: `Idle/SlotFilling/AwaitingApproval/Completed/Cancelled`.
- **`WorkflowStatuses`**: `Question/Confirmation/Completed/Cancelled`.

---

## 9. Güvenlik Katmanları (Özet)

1. **Araç izolasyonu (blast radius)** — Her uzman/akış ajanı yalnızca kendi `ToolGroup`'undaki MCP araçlarını görür (`AgentCard.ToolGroup`, `McpToolProvider.GetTracedToolsForGroupAsync`).
2. **Fail-fast araç doğrulaması** — `ToolRegistry.ValidateDiscoveredMcpTools`, registry ile canlı MCP server araç listesi ayrışırsa başlangıçta patlar.
3. **Deklaratif akış çakışma koruması** — `WorkflowRegistry.RegisterDeclarativeWorkflow`, derlenmiş bir akışın adını YAML ile ezmeye izin vermez.
4. **HITL onay kapısı** — Her para hareketi `RequestPort` üzerinden geçer; LLM eylemi doğrudan tetikleyemez (`DeterministicActionWorkflow`).
5. **İdempotency** — `SessionState.IdempotencyToken` + `CompletedOutcome`, çift onayda (çift tıklama) eylemin yalnızca bir kez çalışmasını garanti eder.
6. **Oturum izolasyonu** — `AmbientSessionAccessor`, farklı bir `sessionId` ile iç içe scope açılmasını reddeder.
7. **Banka-taraflı doğrulama** — Eksekütörlerin `ValidateFilledSlotsAsync` metodu hiçbir kural içermez; her karar `Banking.Domain` servislerine aittir (`BankValidationResult`). Ayrıca `Charge`/`Transfer` yürütmeden önce **aynı** doğrulama yolunu tekrar çalıştırır, böylece ön-doğrulama ile yürütme kuralları asla birbirinden sapmaz.
8. **6 katmanlı çıktı bariyeri** — `ResponseSanitizer`, LLM'in prompt'u ihlal ettiği durumlarda bile araç adı/CoT/XML/prompt sızıntısı/endpoint/token'ı deterministik regex ile temizler (bkz. Bölüm 6).

---

## 10. Yeni Bir Şey Eklerken

- **Yeni derlenmiş akış:** `[Workflow("ad")]` etiketli bir `record` (Banking.Domain) + `IDeterministicActionExecutor<TSlots>` uygulayan **tek bir sınıf** (Banking.Agents/Workflows/Actions) + `Program.cs`'de tek satır DI kaydı. `WorkflowRegistry.AutoDiscoverWorkflows` otomatik bulur — motor/yönlendirme/oturum/prompt kodu değişmez.
- **Yeni deklaratif akış:** `POST /api/workflows/register-declarative`'e YAML/JSON gönder ya da `declarative-agents/*.yaml` dosyası bırak; ad derlenmiş bir akışla çakışamaz.
- **Yeni MCP aracı:** `*Tools` sınıfına `[McpServerTool]` metodu ekle, `Program.cs`'de `.WithTools<T>()` kaydet; `ToolRegistry` otomatik keşfeder.
- **Yeni uzman ajan:** Static `AgentCard Card` + static `Build(IChatClient, IList<AITool>)` olan bir sınıf yaz; `SpecialistCatalog` reflection ile bulur, `Program.cs` A2A endpoint'ini aynı karttan kaydeder.

---

## 11. Çalıştırma

```powershell
$env:OPENAI_API_KEY = "sk-..."
dotnet run --project src/Banking.Host
# http://localhost:5080
```

- `dotnet build BankingAgents.sln` — tüm çözümü derler.
- `dotnet run --project tools/WorkflowTest` — LLM çağrısı yapmadan HITL onay/red senaryolarını simüle eden manuel konsol koşucusu.
- `dotnet run --project tools/ApiDump` — Kurulu MAF/MCP/A2A paketlerinin reflect edilmiş API yüzeyini `maf_api.txt`'ye döker.
- Otomatik test projesi yoktur; `test.http` ile `/api/chat`, `/api/diag`'a örnek istekler atılabilir.
