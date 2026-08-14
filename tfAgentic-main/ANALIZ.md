# Detaylı Mimari, Akış ve Fonksiyon Dokümanı — MAF Agentic Bankacılık Asistanı

> **Teknoloji Yığını:** Microsoft Agent Framework (MAF) · .NET 9 · OpenAI uyumlu `IChatClient` (OpenAI / Ollama) · Model Context Protocol (MCP) · Agent-to-Agent (A2A) · ASP.NET Core Minimal API · Vanilla JS Web UI
>
> **Kapsam:** Bu doküman sistemi, **bir kullanıcı mesajının HTTP isteği olarak girdiği andan yanıtın HTTP cevabı olarak çıktığı ana kadar** izlediği gerçek çağrı sırasına göre anlatır. Her sınıf ilk gerçekten çağrıldığı noktada tanıtılır; her fonksiyon tek cümlelik "ne yapar / nasıl yapar" açıklamasıyla listelenir.
>
> **Bu sistem var olan bir bankaya agentic katman ekler.** `src/Banking.BankApi` geçici bir vekildir ve silinecektir; bankaya giden her yol `IBankApiClient` üzerinden HTTP'dir. Ayrıntı: `CLAUDE.md` → *Target architecture*.

---

## 0. Uygulama Açılışı — Composition Root

Süreç ayağa kalkarken şu adımlar sırayla çalışır.

**`Program.cs`** (`src/Banking.Host/Program.cs`) — Yalnızca sırayı bilir, içerikleri kendi dosyalarındadır:

1. `builder.AddBankingAgents()` → tüm DI kayıtları (`Composition/ServiceRegistration.cs`) ve dönen `AgentOptions`.
2. Kestrel, **`Agents:SelfBaseUrl`'den türetilen** portta tüm ağ arayüzlerini dinler. Port ikinci bir yerde yazılı değildir; süreç kendi MCP ve A2A uçlarına aynı adresten bağlanır.
3. `app.LoadDeclarativeDefinitionsAsync()` → semantik indeksler + diskteki YAML tanımları (`Composition/StartupLoader.cs`).
4. Statik dosyalar (`wwwroot/index.html`) ve `app.MapBankingApi()` (`Composition/ApiEndpoints.cs`).
5. `app.ScheduleAgentWarmUp()` → sunucu dinlemeye başlayınca ajan grafiğini ön ısıtır.

**`ServiceRegistration.AddBankingAgents`** — Kompozisyon kökü; hangi arayüzün hangi uygulamayla karşılandığı **yalnızca burada** yazar:

1. **JSON encoder** — Türkçe karakterlerin `\uXXXX` kaçışı yerine doğal UTF-8 çıkması.
2. **User Secrets + `AgentOptions`** — `Agents` bölümü okunur; `ApiKey` boşsa `OPENAI_API_KEY` ortam değişkenine düşülür.
3. **`CustomerContext`** — isteği yapan müşterinin kimliği; `BankApi:DefaultCustomerId` ayarından gelir, **sessiz varsayılanı yoktur**.
4. **Semantik arama** — `LocalEmbedder`, `ISemanticIndexStore → InMemorySemanticIndexStore` (hesaplanmış vektörlerin depolandığı seam), `MkCodeCatalog`, `SemanticMkCodeSearcher`, `SemanticCampaignSearcher`, `SemanticFinancingSearcher`, `SemanticWorkflowSearcher`, `ICampaignSyncService`. Dördü de tek bir `SemanticIndex<T>` üzerinde çalışır, dış ağa çıkmaz ve içerik değişmediyse depodan yükleyip yeniden vektörleştirmez.
5. **LLM altyapısı** — `TraceHub` ve `TracedChatClientFactory`. Ham istemciler model adına göre önbelleklenir (`AgentOptions.Models` haritasıyla ajan etiketi başka bir modele yönlendirilebilir); ajan kuran her bileşen istemcisini `Create(label)` ile alır ve her istemci `TracingChatClient` ile sarılıdır.
6. **Uzman/orkestratör** — `McpToolProvider`, `SpecialistCatalog`, `NavigationTools`, `AgentRuntime`. Uzman ve iş akışı ajanları burada **kurulmaz** — ikisi de ilk istendiğinde kurulup `IMemoryCache`'te kayan süreyle (`AgentOptions.AgentIdleTimeoutMinutes`) tutulur.
7. **Oturum yönetimi** — `IMemoryCache`, `ISessionStore → InMemorySessionStore`, `AmbientSessionAccessor`. Redis'e geçiş burada tek satır değişikliktir.
8. **İş akışı motoru** — `WorkflowAgentFactory`, `WorkflowRegistry`, `OrchestratorFallback` delegesi ve `Lazy<OrchestratorFallback>`.
9. **Banka istemcisi** — `AddBankApiClient(configuration)`; ardından `IBankApiCallObserver → TraceHubBankApiObserver` ve `RestWorkflowExecutor`.
10. **Gateway** — `FlowRouter`, `GatewayResponseMapper`, `GatewayService`, `ResponseSanitizer`.
11. **MCP server** — `/mcp` altında `AccountsTools`, `CardsTools`, `AnalyzeTools` (loopback HTTP).
12. **A2A host** — `SpecialistRoutingAgent` ile **tek** yol parametreli endpoint. Uzman başına endpoint açılmaz: uzman listesi çalışma zamanında değişebildiğinden endpoint tablosuna bağlanamaz.

> `ToolRegistry` **DI'a kaydedilmez.** Ona ulaşmanın tek yolu `ToolRegistry.Instance`'tır; araç adları DI konteynerinden bağımsız olarak, ajan kurulumu sırasında okunur.

**`StartupLoader.LoadDeclarativeDefinitionsAsync`** — Ekran kodu, kampanya ve finansman indeksleri **bankadan HTTP ile** okunur (banka o an ayakta değilse indeks boş kalır ve ilgili araç ilk çağrıldığında yeniden dener — açılış durdurulmaz). Ardından `declarative-agents/*.yaml|json` ve `specialist-agents/*.yaml|json` diskten okunup kaydedilir; akış indeksi vektörize edilir. Deklaratif uzman kayıtları burada **ajan kurmaz**, yalnızca kartı yazar — kurulum ilk istekte `SpecialistCatalog.GetSpecialistAsync` ile yapılır. **Bozuk bir tanım dosyası açılışı durdurmaz**, gerekçesi loglanır.

**`StartupLoader.ScheduleAgentWarmUp`** — `ApplicationStarted` olayında arka planda `AgentRuntime.EnsureReadyAsync()` çağrılır; bu, orkestratörü kurar ve `SpecialistCatalog.EnsureInitializedAsync` ile süreç içi MCP bağlantısını hazırlar (uzman ajanların kendisi kurulmaz — ilk gerçek istekte kurulur). Ön ısıtma daha erken yapılamaz: MCP loopback bağlantısı sunucu dinlemeye başlamadan kurulamaz. Başarısızlık ölümcül değildir; ilk istek yeniden dener.

### HTTP Endpoint'leri

| Endpoint | Görev |
|---|---|
| `POST /api/chat` | Web UI sohbet girişi (`message`/`text` + `sessionToken`) → `GatewayService.ProcessUserRequestAsync` |
| `POST /api/ai/chat` | Java MW gateway girişi (`GatewayRequest` şeması) → aynı metot |
| `POST /api/workflows/register-declarative` | Çalışma zamanında YAML/JSON akış kaydı; diske de yazılır ve anında indekslenir |
| `POST /api/specialists/register-declarative` | Çalışma zamanında uzman ajan kaydı; aynı desen |
| `GET /api/specialists` | Uzman kartları (UI kartları için) |
| `GET /api/tools` | MCP araç kataloğu (uzman tanımlarken seçilebilecek araçlar) |
| `POST /api/campaigns/sync` | Kampanya semantik indeksini bankadan tazeler |
| `GET /api/diag` | `AgentRuntime.GetDiagnosticsAsync` — MCP/uzman/A2A wiring sağlık kontrolü (anahtarsız da çalışır) |
| `GET /api/trace/stream` | `TraceHub` SSE kanalı — UI'ın canlı iz paneli buna abone olur |
| `/mcp`, `/a2a/{route}` | Protokol uçları |

---

## 1. Mesajın Girişi — `GatewayService`

**`GatewayService`** (`src/Banking.Gateway/GatewayService.cs`) — Kullanıcı isteklerinin **giriş noktası ve yanıtların tek çıkış noktası**. Bağımlılıkları: `FlowRouter`, `TraceHub`, `ILogger`, `ResponseSanitizer`, `GatewayResponseMapper`, `AmbientSessionAccessor`.

- `ProcessUserRequestAsync(request, ct)`: Oturum jetonunu normalize eder (boşsa yeni üretir), **oturum scope'unu açar**, işi `FlowRouter`'a devreder, dönen sonucu `GatewayResponseMapper` ile UI protokolüne çevirir. İptal (`OperationCanceledException`) ve beklenmeyen hata dâhil **her yol** kullanıcıya gösterilebilir bir yanıtla biter.
- `FinalizeResponse(response, sessionToken)` (private): Yanıta oturum jetonunu ekler ve `ResponseSanitizer`'dan geçirir. **Temizlenmemiş hiçbir metin istemciye ulaşmaz.**

Scope açılışı iki adımdır ve bu ayrım zorunludur:

```csharp
var lease = await sessionAccessor.OpenSessionScopeAsync(sessionToken, cancellationToken);
await using var session = lease.EnterAmbientScope();
```

---

## 2. Oturum — Session, Scope ve StateBag

### 2.1 `SessionState` — ham veri

**`SessionState`** (`Sessions/SessionState.cs`) — Bir oturumun taşıdığı durumun tamamı; **saf veri tipi, davranış içermez**. Ajanlar singleton ve stateless olduğundan kullanıcıya özel her değer burada tutulur.

| Alan | Tip | Ne taşır |
|---|---|---|
| `SessionId` | `string` | Oturum kimliği |
| `ActiveWorkflowName` | `string?` | Aktif akışın kanonik adı |
| `Phase` | `string` | `idle` / `slot_filling` / `awaiting_approval` / `completed` / `cancelled` |
| `SlotValues` | `Dictionary<string, object?>` | Toplanan ham slot değerleri (StateBag) |
| `ExpectedSlots` | `Dictionary<string, DeclarativePropertyConfig>?` | Akışın YAML şeması: ad → tip + zorunluluk |
| `ProposedSlots` | `Dictionary<string, object?>?` | Onaya **açıkça** sunulmuş küme |
| `PendingConfirmation` | `GenericConfirmationView?` | Onay ekranına gönderilen görünüm |
| `IdempotencyToken` | `string?` | Teklifin parmak izi — çift tıklama koruması |
| `CompletedOutcome` | `WorkflowOutcome?` | Bu anahtar için üretilmiş sonuç |
| `SerializedOrchestratorSession` | `JsonElement?` | Orkestratörün konuşma geçmişi |
| `SerializedWorkflowSession` | `JsonElement?` | Akış ajanının konuşma geçmişi |
| `LastUserMessage` | `string?` | Bu isteğin mesajı; `start_workflow` aracı okur |
| `PendingAgentSignal` | `string?` | Akış ajanının bu turda bıraktığı denetim sinyali |

**Neden şema ile teklif ayrı alanlarda?** `ExpectedSlots` "ne bekleniyor", `ProposedSlots` "ne sunuldu" bilgisidir. Tek alanda birleşmeleri, onay ekranının boş değerlerle üretilmesine yol açar.

### 2.2 `SessionContext` — oturuma giden **tek kapı**

**`SessionContext`** (`Sessions/SessionContext.cs`) — Oturum verisine erişen her katman (orkestratör, motor, slot yazıcı) bu tipten geçer. Metotları **senkrondur**: veri bellektedir, I/O yoktur.

- `BeginWorkflow(name)`: Akışı oturuma bağlar, evreyi `idle` yapar. Boş ad kabul etmez.
- `CompleteWorkflow()` / `CancelWorkflow()`: `ResetWorkflow` üzerinden akışı sonlandırır.
- `ResetWorkflow(finalPhase, trace)` (private): Akış durumunu, slotları, şemayı, teklifi, onayı, idempotency'yi, akış ajanı geçmişini **ve** `SerializedOrchestratorSession`'ı temizler. Sonuncusu, tamamlanmış bir akıştan sonra orkestratörün onay bekliyormuş gibi davranmasını engeller — bkz. Bölüm 4.4.
- `SetSlot(name, value)`: Tek slot değerini yazar.
- `GetSlotStatus()`: `SlotBinder.DetermineSlotStatus` ile dolu/eksik/opsiyonel slotları hesaplar.
- `StoreProposedSlots(workflowName, slots)`: Teklifi saklar ve `null` olmayan değerleri StateBag'e geri yazar.
- `DeclareExpectedSlots(schema)`: Akışın YAML şemasını oturuma tanıtır; akış her başladığında yeniden yazılır.
- `RaiseAgentSignal(signal)` / `ConsumeAgentSignal()`: Akış ajanının araç sinyalini yazar / okuyup siler.

### 2.3 Scope — `AmbientSessionAccessor` ve `SessionLease`

**`AmbientSessionAccessor`** (`Sessions/AmbientSessionAccessor.cs`) — `AsyncLocal<SessionContext?>` tabanlı. **Oturum kimliği katmanlar arasında parametre olarak taşınmaz ve araçlara verilmez.** Üç garantiyi birlikte sağlar:

1. **İzolasyon** — farklı bir oturumla iç içe scope açmak istisna üretir.
2. **Tek okuma / tek yazma** — durum açılışta yüklenir, kapanışta yazılır.
3. **Oturum başına serileştirme** — aynı oturuma gelen eşzamanlı istekler sıraya girer. Onay düğmesine çift tıklamayı güvenli kılan kilit budur.

- `Current`: Aktif oturum; scope yoksa istisna. Sessizce yeni oturum üretmek iki kullanıcının verisini karıştırırdı.
- `OpenSessionScopeAsync(sessionId, ct)`: Kilidi alır, durumu **bir kez** yükler, `SessionLease` döner. Aynı oturumla iç içe çağrıldığında yeniden yükleme/kilitleme yapmaz.
- `CloseSessionScopeAsync(lease)` (internal): Durumu yazar, ambient'ı temizler, kilidi bırakır. **İstek iptal edilse veya hata alsa bile yazma yapılır**; o ana kadarki ilerleme korunur.

**`SessionLease`** (`Sessions/SessionLease.cs`) — `IAsyncDisposable`.

- `EnterAmbientScope()`: Oturumu ambient hâle getirir. **Senkron olmak zorundadır:** `async` yapılırsa `AsyncLocal` yazımı yalnızca metodun kendi bağlamında kalır ve `Current` çağrının devamında istisna fırlatır.

### 2.4 Kalıcılık — `ISessionStore` / `InMemorySessionStore`

**`ISessionStore`** (`Sessions/ISessionStore.cs`) — Tek çağıranı `AmbientSessionAccessor`'dır. Sistemde kalan **tek tek-uygulamalı arayüzdür** ve bilerek korunmuştur: Redis'e geçişin belgelenmiş tek DI satırıdır, `ValueTask` imzaları da bunun içindir.

**`InMemorySessionStore`** — `IMemoryCache` üzerinde kayan TTL (`Sessions:SlidingTimeToLiveMinutes`, varsayılan 30 dk). `PublishEviction` geri çağrımı, yarım kalan bir akışın düştüğü anı ize yazar; kayıp yalnızca bu satırda görünür.

---

## 3. Yönlendirme Kararı — `FlowRouter`

**`FlowRouter`** (`src/Banking.Gateway/FlowRouter.cs`) — Bağımlılıkları: `AgentRuntime`, `WorkflowRegistry`, `AmbientSessionAccessor`, `TraceHub`, `ILogger`.

- `RouteUserRequestAsync(userMessage, ct)`: `sessionAccessor.Current.HasActiveWorkflow` değerine bakar.
  - **`true`** ise: `WorkflowRegistry.CreateEngineAsync(activeWorkflowName)` ile bu isteğe ait motoru kurar ve `AdvanceWorkflowStepAsync`'i çağırır. Mesaj **hiçbir koşulda orkestratöre uğramaz** — ödeme ortasındaki kullanıcı LLM akıl yürütmesiyle akıştan çıkarılamaz.
  - **`false`** ise: `AgentRuntime.AskOrchestratorAsync`.

`FlowExecutionResult` (record) hangi hattın çalıştığını (`WorkflowStep` / `OrchestratorReply`) ve hangi alanın dolu olduğunu belirtir.

---

## 4. Yol A — Aktif Akış Yokken: Orkestratör

### 4.1 `AgentRuntime` — orkestratörün tek giriş noktası

**`AgentRuntime`** (`Runtime/AgentRuntime.cs`) — Bağımlılıkları: `AgentOptions`, `TraceHub`, `ILogger`, `SpecialistCatalog`, `NavigationTools`, `McpToolProvider`, `AmbientSessionAccessor`, `TracedChatClientFactory`, `SemanticWorkflowSearcher`, `WorkflowRegistry`.

- `EnsureReadyAsync(ct)`: Uzmanları ve orkestratörü kurar; kurulu ve güncelse etkisizdir. **Yeniden kurulumun tek nedeni uzman kataloğunun sürüm değişimidir** — köprü araçları ve yönlendirme kuralları kartlardan üretilir. Başarısız kurulum kalıcı değildir; ilk istek yeniden dener.
- `BuildOrchestrator()` (private): Uzman köprüleri + `start_workflow` + ekran araçlarıyla `ChatClientAgent` kurar. Sistem talimatı kullanıcıya özel bilgi içermez, bu yüzden tek örnek tüm istekleri karşılar.
- `AskOrchestratorAsync(userMessage, ct)`: Mesajı oturuma yazar, aday akışları arar (**aktif akış varken aday listesi verilmez**, devam eden işlem korunur) ve model turunu çalıştırır.
- `AskLanguageModelAsync(...)` (private): Aday akış bağlamını ve önceki tur bağlamını sistem mesajı olarak ekler, konuşmayı oturumdan geri yükler, ajanı çalıştırır, geçmişi tekrar oturuma yazar, tetiklenen aracı çıkarır ve `StoreTurnContext` ile bağlamı saklar/temizler.
- `SearchCandidateWorkflows(userMessage)` (private): `topK: 3` aday döner. **Arama akış başlatmaz** — vektör benzerliği *konu* yakınlığını ölçer, *niyeti* değil.
- `StartWorkflowAsync(workflowName, ct)` (private, LLM aracı): Oturumda akış varsa başlatmaz ve durumu araç sonucu olarak modele bildirir; yoksa `BeginWorkflow` + `CreateEngineAsync` + `AdvanceWorkflowStepAsync`.
- `GetDiagnosticsAsync(ct)`: Anahtar, model, MCP URL/araç sayısı, uzman listesi, taşıma katmanı ve orkestratör durumu.

### 4.2 Uzmanlar — `SpecialistCatalog` ve `AgentCard`

**`AgentCard`** (record) — Bir uzman hakkındaki **tek doğruluk kaynağı**: ad, açıklama, talimat, araç grubu, köprü aracı adı, A2A yolu, görünen ad, isteğe bağlı açık araç listesi.

**`SpecialistCatalog`** (`Specialists/SpecialistCatalog.cs`) — Uzmanların keşfi, kurulumu ve bağlanma biçiminin tek yönetim noktası.

- `Cards`: Derlenmiş kartlar (**statik olarak bir kez** reflection ile bulunur ve önbelleklenir) + deklaratif kartlar (`ConcurrentDictionary`, çalışma zamanında eklenebilir).
- `EnsureInitializedAsync(ct)`: Uzmanları **kurmaz** — yalnızca süreç içi MCP bağlantısını hazırlar ve taşıma katmanını belirler. Başarısız hazırlık kalıcı değildir.
- `GetSpecialistAsync(name, ct)`: Ajanı `IMemoryCache`'ten döner; önbellekte yoksa (ilk istek veya boşta kalıp tahliye edilmiş) `BuildFromCardAsync` ile kurup kayan süreyle (`AgentOptions.AgentIdleTimeout`, varsayılan 20 dk) önbelleğe yazar. Ad başına `SemaphoreSlim` eşzamanlı ıskaları tekilleştirir. Tahliye `Version`'ı **artırmaz**.
- `RegisterDeclarativeSpecialistAsync(definition, ct)`: Kartı yazar, katalog hazırsa ajanı hemen kurar, `Version`'ı artırır.
- `GuardAgainstCompiledCollision(card)` (private): Ad, köprü aracı adı **ve** A2A yolu için çakışma denetler; üçü de çağrının hangi uzmana gideceğini belirler.
- `BuildBridgeTools()`: Her uzman için çağıranın kullanabileceği aracı üretir. Süreç içi modda somut ajanı değil `LazySpecialistAgent` vekilini sarar; vekil hedefi her çağrıda adına göre çözer, böylece köprü aracı boşta kalan ajanın tahliyesini engellemez.
- `BuildFromCardAsync(card, ct)` (private): Kartı çalışan ajana çevirir. **Derlenmiş ve deklaratif uzman aynı biçimde kurulur**; uzman başına `Build` metodu yoktur, kart sözleşmenin tamamıdır.

Derlenmiş uzmanlar (`HesapUzmaniAgent`, `KartUzmaniAgent`, `AnalizUzmaniAgent`) yalnızca statik bir `AgentCard Card` özelliği taşır.

**`SpecialistRoutingAgent`** (`Specialists/SpecialistRoutingAgent.cs`) — Tek `/a2a/{route}` endpoint'inin arkasındaki dağıtıcı ajan; hedefi istek yolundan çözer. Tek endpoint kullanılmasının nedeni A2A endpoint tablosunun uygulama başlatılırken sabitlenmesidir.

### 4.3 MCP Bağlantısı — `McpToolProvider`

**`McpToolProvider`** (`Mcp/McpToolProvider.cs`) — Süreç içi MCP sunucusuna loopback HTTP ile bağlanan tek istemci. Bağlantı **tembel ve tek seferlik** kurulur.

- `EnsureConnectedAsync(ct)` (private): Bağlantıyı kurar, araçları keşfeder ve `ToolRegistry.ValidateDiscoveredMcpTools` ile fail-fast doğrular.
- `GetTracedToolsForGroupAsync(group, ct)`: Gruba ait araçları izleme sarmalayıcısıyla döner.
- `GetTracedToolsByNamesAsync(names, ct)`: Adları verilen araçları döner. **Bilinmeyen bir ad atlanmaz**, istisna fırlatır.

### 4.4 Bağlamsal Devamlılık — önceki tur bağlamı

Zamirli takip cümlelerinin ("ikincisini", "bunlardan bana uygun olanı") göndergesi, orkestratörün bir önceki turdaki yanıtından çözülür. Bu iş `ContextProviders/TopicContextProvider` içinde yapılır; sağlayıcı orkestratöre `ChatClientAgentOptions.AIContextProviders` ile bağlanır (`AgentRuntime.BuildOrchestrator`).

- Sağlayıcı turdan **önce** önceki yanıtı bağlam olarak ekler, turdan **sonra** yeni yanıtı saklar. İki yarı aynı tipte durduğundan ayrışamaz.
- Turda `start_workflow` veya `open_screen` tetiklendiyse bağlam temizlenir (konu kapandı). Karar **araç adı sabitlerinden** okunur (`ContextResettingTools`), yanıt metninden değil: metinde anahtar sözcük aramak kullanıcının olağan cümlesinde de eşleşir.
- Akışın tamamlanması ve iptali bu listede yoktur; aktif akışta yönlendirici orkestratörü baypas ettiği için sağlayıcı o turları hiç görmez. Temizliği `SessionContext.ResetWorkflow` yapar (bkz. §3.2).

### 4.5 Prompt Deposu — `AgentPrompts`

**`AgentPrompts`** (`Prompts/AgentPrompts.cs`) — Tüm ajan talimatlarının tek kaynağı. Ortak bloklar (`PresentationContract`, `WorkflowGuardrails`, `SpecialistGuardrails`) tek yerde tutulur ve her talimatta yeniden kullanılır.

- `GetOrchestratorInstructions(dynamicRoutingRules)`: Uzman yönlendirme kuralları kartlardan üretilerek gömülür. Talimat hiçbir akış adı saymaz.
- `BuildCandidateWorkflowsContext(candidates)`: Aday akışları per-request bağlam mesajına çevirir; listenin bir **ad sözlüğü** olduğunu, başlatma gerekçesi olmadığını açıkça yazar.
- `BuildSlotFillingRules(propose, cancel, handoff)`: Akış ajanının kuralları; araç adları çağırandan gelir, burada yeniden türetilmez.

### 4.6 İzleme — `TraceHub` / `TracingAIFunction` / `TracingChatClient`

- **`TraceHub`** — Tüm katmanlardan gelen iz olaylarının merkezi yayın noktası; hem `ILogger`'a yazar hem SSE abonelerine gönderir. Abone kanalları sınırlı (512) ve `DropOldest`.
- **`TracingAIFunction`** — Bir aracı saran dekoratör; çağrı öncesi argümanları, sonrası sonucu yayınlar. Aracın davranışını değiştirmez; metin dönen bir sonucu ize yazmadan önce `Unwrap` ile ham metne indirger — aksi hâlde `JsonElement` sarmalı sonuç izde bir kez daha kaçışlanıp okunmaz hale gelir.
- **`TracingChatClient`** — `DelegatingChatClient`; her model çağrısını ve yanıtı yayınlar. `PrepareTurn` iki çağrı yolunun (senkron/streaming) ortak hazırlığını yapar: varsayılan sıcaklık yalnızca istek kendi değerini belirtmediğinde uygulanır. Her tamamlanan tur ayrıca bir `"{etiket} -> Token Maliyeti"` satırı yayınlar (model adı, girdi/çıktı/toplam token — sağlayıcı döndürdüyse — ve süre); sistemin donanım faturasını belirleyen çarpan mesaj başına model turu olduğundan bu ölçüm her fazın kanıtıdır.
- **`TraceHubBankApiObserver`** — `IBankApiClient`'ın istek/yanıt/hata izlerini `TraceHub`'a bağlar. Yayın hatası banka çağrısını düşürmez.

---

## 5. Yol B — Aktif Akış: `WorkflowRegistry` → `WorkflowEngine`

### 5.1 Katalog ve Motor Fabrikası — `WorkflowRegistry`

**`WorkflowRegistry`** (`Workflows/Engine/WorkflowRegistry.cs`) — Bağımlılıkları: `WorkflowAgentFactory`, `AmbientSessionAccessor`, `RestWorkflowExecutor`, `Lazy<OrchestratorFallback>`, `TraceHub`.

**Tek kayıt yolu MAF deklaratif YAML/JSON tanımıdır.** Yeni bir akış eklemek bir metin dosyası yazmaktan ibarettir: C# record'u, akışa özel executor, attribute veya DI kaydı gerekmez.

- `RegisterDeclarativeWorkflow(definition)`: Metadata'yı ve `WorkflowActionDefinition`'ı (ad, mkCode, HTTP metodu, yol, görünen ad) yazar. Aynı ad ikinci kez gönderildiğinde üzerine yazılır.
- `CreateEngineAsync(workflowName)`: Bu isteğe ait motoru üretir; kayıtlı değilse `KeyNotFoundException`.
- `BuildEngineAsync(registered)` (private): **Sıra bağlayıcıdır** — önce şema oturuma tanıtılır (`DeclareExpectedSlots`), sonra ajan kimliği kurulur (`WorkflowAgentDefinition.For`), talimat üretilir, ajan alınır, en son motor bağlanır.

**Kayıt defteri davranış değil veri tutar.** Akışın yolu, şeması ve talimatı sözlükte okunabilir durur; motoru bunlardan `BuildEngineAsync` kurar.

### 5.2 Ajan Kurucusu — `WorkflowAgentFactory`

**`WorkflowAgentFactory`** (`Workflows/Engine/WorkflowAgentFactory.cs`) — İş akışı ajanlarını kurar ve **ada + talimat parmak izine + uzman sürümüne** göre önbellekler. Yalnızca ada bakmak, YAML güncellendiğinde ya da uzman listesi değiştiğinde eski ajanın dönmesine yol açardı.

Ajanın araç kümesi: uzman köprüleri + dört denetim aracı.

- `BuildUpdateSlotTool(definition)` (private): `SchemaValidatedSlotWriter` üzerinden yazar; araç açıklamasına beklenen tipleri ekler.
- `BuildProposeTool(definition)` (private): `GetSlotStatus()` tamamsa `ToProposedSlots()` ile kümeyi kurar, oturuma yazar ve `PROPOSAL_READY:` + JSON döner; eksikse hangi alanların sorulacağını bildirir.
- `BuildCancelTool` / `BuildHandoffTool` (private): Sinyali oturuma yazar ve aynı değeri araç sonucu olarak döner.

**Akış ajanı banka verisine doğrudan erişmez;** ihtiyaç duyduğu her bilgiyi uzmanlara sorar. Bir akış tanımı yazmak, o akışa banka araçlarının tamamını açmak anlamına gelmez.

`OllamaCompatibleSchemaOptions`: `update_slot`'un `value` parametresi `object` olduğundan üretilen şema çıplak `true` düğümüdür; Ollama bunu reddeder. Boş nesneye (`{}`) indirgenir — anlamca eşdeğer, iki tarafça da kabul edilir.

### 5.3 Çekirdek Motor — `WorkflowEngine`

**`WorkflowEngine`** (`Workflows/Engine/WorkflowEngine.cs`) — **Tüm iş akışlarının tek motoru; generic değildir.** Akışa özgü kod veya tip içermez; akıştan akışa değişen tek girdi YAML'dan gelen `WorkflowActionDefinition`'dır. **Durum tutmaz**, her HTTP isteğinde yeniden yaratılır.

- `AdvanceWorkflowStepAsync(userMessage, ct)`: Tek genel metot; `session.Phase`'e göre üç özel metottan birine dallanır.

| Evre | Metot | Ne yapar |
|---|---|---|
| başlangıç | `StartWorkflowAndCollectFirstSlotsAsync` | Slot doldurmaya geçirir; tetikleyen cümleyi ajana verir ki verilmiş değerler tekrar sorulmasın |
| `slot_filling` | `CollectSlotsAsync` | Kullanıcı yanıtını + slot özetini + tur yönergesini ajana iletir |
| `awaiting_approval` | `ApplyApprovalDecisionAsync` | Onay kararını uygular; anlaşılmayan yanıt **onay sayılmaz** |

- `InterpretAgentOutputAsync(...)` (private): Turu yorumlar. **Kontrol sırası bağlayıcıdır:** iptal → devretme → teklif → soru. Devretme teklif kontrollerinden önce gelir, çünkü konu dışı bir turda toplanan parametrelere dokunulmamıştır; onlarla onay ekranı açmak kullanıcıyı istemediği bir işlemin onayına götürürdü. **Karar araç sinyallerinden okunur, yanıt metninden değil** — model araç sonucunu metnine kopyalamaz.
- `ConcludeSlotFillingAsync(slots, ct)` (private): Onay ekranını kurar, idempotency jetonunu hesaplar. **Onay kapısı yalnızca bankaya istek gönderen akışlar için kurulur** (`action.CallsBank`); ayrım YAML'daki `action.path` alanından okunur, akış adı sorulmaz.
- `RunActionGraphAsync(...)` (private): MAF grafiğini kurar, onay kararını porta **aynı istek içinde** iletir ve çıktı/hata event'lerini işler. Executor içindeki istisnalar çağırana fırlamaz, event akışına düşer — bu yüzden `ExecutorFailedEvent` ve `WorkflowErrorEvent` açıkça ele alınır.
- `FinishWorkflow(outcome)` (private): Sonucu adım sonucuna çevirir ve oturumu temizler.
- `RunAgentTurnAsync(message, ct)` (private): Konuşmayı oturumdan geri yükler, ajanı bir tur çalıştırır, geçmişi tekrar yazar.

### 5.4 Slot Mekanizması — `Slots/`

**`SlotStatus`** (record, `Slots/SlotBinder.cs`) — Alanları `FilledSlots`, `MissingSlots`, `OptionalSlots`.
- `IsComplete`: Zorunlu slotların tamamı dolu mu. **Bu bir C# karşılaştırmasıdır, modelin kanaati değil.**
- `ToProposedSlots()`: Onaya sunulacak kümeyi üretir.
- `DescribeMissingSlots()`: Eksikleri ajanın sorabileceği biçimde özetler.
- `DescribeForAgentTurn()`: Durumu ajanın turuna eklenecek tek satırlık özete çevirir.

**`SlotBinder`** (static, internal) — Toplanan değerler ile YAML şeması arasındaki **saf** karşılaştırma; bağımlılığı, durumu ve yan etkisi yoktur.
- `DetermineSlotStatus(slotValues, expectedSlots)`: Dolu/eksik/opsiyonel ayrımını yapar.
- `ResolveSlotNames(...)` (private): Toplanan anahtarlar ∪ şemadaki slotlar. **Şemada olmayan ama model tarafından yazılmış anahtarlar da görünür kalır**; yanlış slot adı üreten model böylece izde teşhis edilir.
- `IsRequired(...)` (private): Şemada tanımlı olmayan slot zorunlu sayılmaz — modelin fazladan yazdığı bir anahtar akışı kalıcı olarak "eksik" durumunda bırakamaz.
- `IsBlank(value)` (private): Boş metin ya da sıfır/negatif tutar.

### 5.5 Şema Kapısı — `Validation/`

**`SchemaValidatedSlotWriter`** (`Validation/SchemaValidatedSlotWriter.cs`) — `update_slot`'un arkasındaki **tek geçiş noktası**. Oturuma yazılan her slot değeri buradan geçer.

- `WriteSlotIfSchemaAccepts(slotName, value)`: Değeri YAML'daki tiple karşılaştırır; uymuyorsa **yazmaz** ve gerekçeyi araç sonucu olarak ajana döner.
- `MatchesDeclaredType(...)` / `ResolveValueKind(...)` (private, static): Tip denetimi. `value` parametresi `object` olduğundan System.Text.Json her zaman `JsonElement` üretir; ayrım `ValueKind` üzerinden yapılmalıdır.

**Uygulanan tek kural şema uyumudur.** Yetersiz bakiye, borcu aşan tutar ve geçersiz IBAN denetimleri bankanın kendi uçlarına aittir; burada tekrarlanmaları iki kopyanın birbirinden habersiz sapması demektir. **Değer dönüştürülmez, reddedilir** — uymayan bir değerin biçimlendirilerek kabul edilmesi, kullanıcının söylemediği bir değerin onay ekranına taşınması riskini doğurur.

### 5.6 Onay Kapısı (HITL) — `Approval/`

**`ApprovalIntentParser`** (static, partial) — Onay yanıtını `Approved` / `Rejected` / `Unclear`'a çevirir. **Değerlendirme sırası bağlayıcıdır:** önce olumsuzluk aranır, çünkü Türkçede olumsuzluk eki fiilin içindedir ("onaylamıyorum") ve alt dize eşleştirmesi olumsuz bir yanıtı olumlu okur. Sonra tam kelime eşleşmesiyle olumluluk. İkisi de eşleşmezse `Unclear` — belirsiz yanıt **hiçbir koşulda onaya düşmez**.

**`DeterministicActionWorkflow`** (static) — MAF grafiğini kurar: `RequestPort` onay kapısı → `DeterministicActionStep`. **Para hareketi üreten hiçbir çağrı bu kapıdan geçmeden çalışmaz;** model eylemi doğrudan tetikleyemez, yalnızca onay ekranını hazırlayabilir.

**`DeterministicActionStep`** (`Executor<bool, WorkflowOutcome>`) — Grafikteki tek yürütme düğümü; onay kararını girdi alır ve eylemi model müdahalesi olmadan çalıştırır.

### 5.7 Eylem — `Actions/`

**`WorkflowActionDefinition`** (record) — Akışın kimliğinin ve hedefinin veri hâli: `WorkflowName`, `MkCode`, `Method`, `Path`, `DisplayName`.
- `CallsBank`: `Path` dolu mu — onay kapısının kurulup kurulmayacağını bu belirler.
- `Label`: Kullanıcıya gösterilecek ad.

**`RestWorkflowExecutor`** (`Workflows/Actions/RestWorkflowExecutor.cs`) — **Sistemdeki tek executor.** Durumsuz ve singleton; akışın kimliği ve hedefi her çağrıda parametre olarak gelir. Yeni bir akış eklemek bu sınıfta değişiklik gerektirmez.

- `BuildConfirmationView(action, slots)`: Onay ekranını YAML'da tanımlı slotlardan kurar.
- `ExecuteBankActionForApprovalDecisionAsync(action, slots, approved, ct)`: Onay yoksa bankaya istek atmaz. **Hiçbir koşulda istisna sızdırmaz** — banka reddi ve ağ hatası dâhil tüm başarısızlıklar `WorkflowOutcome.Failure` olarak döner; eylem MAF grafiğinin içinde çalıştığı için oradan fırlayan bir istisna çağırana ulaşmaz, event akışına düşer. Yol bankada bulunamazsa (`404`) genel banka-reddi dalından ayrı yakalanır: YAML'daki `action.path` bankanın güncel yüzeyiyle uyuşmuyor demektir (ör. gerçek bankaya geçişte unutulan bir eşleme) — bu, tanı gerektiren bir yapılandırma hatasıdır, sıradan bir işlem reddi değil, bu yüzden ize ayrı bir uyarı satırı düşer.
- `BuildReceipt(action, bankResponse)` (private): **Makbuz numarası yalnızca bankadan gelir**; banka numara döndürmediyse makbuz üretilmez, bu katman numara türetmez.

**Bankacılık kuralı içermez.** Tutar karşılaştırması, limit denetimi ve IBAN doğrulaması bankanın kendi uçlarına aittir.

### 5.8 Deklaratif Akışlar — `Declarative/`

**`MicrosoftAgentDeclarativeDefinition`** — MAF deklaratif şemasının C# karşılığı: `Name`, `DisplayName`, `Description`, `Instructions`, `MkCode`, `Action` (`Method`, `Path`), `InputSchema`/`OutputSchema` (`Properties`: ad → tip/açıklama/zorunluluk), `Model.Options` (`Temperature`, `TopP`).

**`DeclarativeWorkflowBuilder`** (static)
- `ParseFromYaml(content)` / `ParseFromJson(content)`: Tanınmayan alanlar yok sayılır.
- `BuildInstructions(definition, agentDefinition)`: Kimlik + özel talimat + parametre listesi + ortak slot doldurma kuralları. **Araç adları `agentDefinition`'dan okunur, burada yeniden türetilmez** — aynı türetme kuralının iki yerde bulunması, talimattaki adla kayıtlı adın ayrışmasına ve akışın hata üretmeden durmasına yol açar.

**`WorkflowAgentDefinition`** (record) — Ajan ve araç adlarının **tek türetildiği yer** (`For(workflowName, ...)`): `{ad}Asistani`, `propose_{ad}`, `cancel_{ad}`, `handoff_{ad}`.

**`WorkflowAgentSignals`** (static, internal) — `ProposalReady` (`"PROPOSAL_READY:"`), `Cancelled`, `Handoff`. Üreten (`WorkflowAgentFactory`) ve okuyan (`WorkflowEngine`) taraflar aynı sabitleri kullanır.

### 5.9 Ortak Sözleşme Tipleri — `Contracts/`

**`WorkflowStepResult`** (record) — Bir adımın gateway'e taşınan sonucu: `SessionId`, `Status`, `Prompt`, `Confirmation`, `Outcome`. Fabrika metotları: `Ask`, `AwaitApproval`, `Completed`, `Ended`.

**`WorkflowOutcome`** (record) — Tüm akışların **ortak sonuç sözleşmesi**: `WorkflowName`, `Status` (`Succeeded`/`RejectedByUser`/`Failed`), `UserMessage`, `ReceiptId`, `Details`. `UserMessage` zorunludur — başarısızlıkta gerçek gerekçeyi taşır ve üst katmanda yutulması mümkün değildir. Fabrika metotları: `Success`, `RejectedByUser`, `Failure`.

**`GenericConfirmationView`** (record) — Onay ekranı görünümü: `WorkflowName`, `Parameters`, `ScreenMkCode`.
- `ComputeIdempotencyToken(view)` (static): Akış adı + sıralı slotlardan SHA-256. **Tek yerde tutulur:** motor bu değeri oturuma yazarak ikinci onay isteğini tanır, executor aynı değeri bankaya `Idempotency-Key` olarak gönderir.

---

## 6. Çıkış — Yanıtın UI'a Dönüşü

**`GatewayResponseMapper`** (`src/Banking.Gateway/GatewayResponseMapper.cs`) — Yönlendirme sonucunu UI protokolüne çevirir. **Bankacılık metni üretmez;** akışın kendi `UserMessage`'ını olduğu gibi taşır.

| Durum | Yanıt tipi |
|---|---|
| `Question` | `TEXT` (slot doldurma sorusu) |
| `Confirmation` | `CONFIRM_PAYMENT` (+ `MkCodeCatalog`'dan çözülen ekran kodu) |
| `Completed` | `TEXT` (makbuz mesajı) |
| `Cancelled` | `TEXT` |
| Orkestratör + `open_screen` | `OPEN_SCREEN` (+ hedef mkCode) |
| Orkestratör (diğer) | `TEXT` |

`BuildConfirmationScreen` **kod çözülemediğinde tahmin yapmaz**: yanlış ekran göstermek yerine akış adı olduğu gibi iletilir ve durum uyarı olarak loglanır.

**`GatewayResponse`** (record) — Kablo üzerindeki gösterim: `type`, `text`, `mkCodeValue`, `sessionToken` + `[JsonExtensionData]` ile kök seviyeye yazılan akışa özgü değerler (iç içe nesne üretilmez).

**`ResponseSanitizer`** (`src/Banking.Gateway/ResponseSanitizer.cs`) — **6 katmanlı deterministik çıktı bariyeri.** Sıra bağlayıcıdır: XML blokları → XML etiketleri → araç adları → düşünce kalıpları → sistem talimatı parçaları → endpoint'ler → oturum jetonları → boşluk normalizasyonu. Blok yapıları satır bazlı kalıplardan önce temizlenmezse blok içeriği metinde kalır. Araç adı listesi `ToolRegistry.Instance.GetAllSanitizableToolNames()`'ten dinamik üretilir.

> **Bakım notu:** XML etiket listesi elle tutulur. Prompt'a yeni bir etiket eklerken `XmlTagPattern` ve `XmlBlockPattern`'e de eklenmelidir; unutulan etiket yakalanmaz.

Temizlenmiş yanıt HTTP 200 ile UI'a döner. Bu sırada `await using` bloğu kapanır: `SessionLease.DisposeAsync` → `AmbientSessionAccessor.CloseSessionScopeAsync` durumu depoya **bir kez** yazar, ambient oturumu temizler ve kilidi bırakır.

---

## 7. MCP Sunucu Katmanı — `Banking.Mcp`

### 7.1 Araç Kayıt Sistemi

**`ToolRegistry`** (`Registry/ToolRegistry.cs`) — Araçların otomatik keşfi ve grup bazlı erişimi. **`ToolRegistry.Instance` ona ulaşmanın tek yoludur**; DI'a kaydedilmez, çünkü tarama tembel ve idempotenttir ve araç adları konteynerden bağımsız olarak okunmalıdır.

- `ScanAssembly(assembly)`: `[McpServerToolType]` taşıyan veya adı `Tools` ile biten sınıfların `[McpServerTool]` metotlarını kaydeder.
- `Get(name)` / `GetAllTools()` / `GetNamesByGroup(group)` / `GetAllSanitizableToolNames()`.
- `ValidateDiscoveredMcpTools(discovered)`: Kayıtlı ama sunucuda bulunmayan araç varsa **fail-fast**. Uyuşmazlık bağlantı kurulumunda yakalanır, aracın ilk çağrısında değil.
- `RegisterNonMcpCoreTools()` (private): Uzman köprüleri, `start_workflow`, `ekran_kodu_ara`, `ekran_ac`.
- `InferGroupFromTypeName(name)` (private): `[ToolGroup]` yoksa addan çıkarım.

**`ToolDefinition`** (record, `Registry/ToolDefinition.cs`) — `Name`, `Group` (`Account`/`Card`/`Analyze`/`Orchestrator`), `Type` (`Mcp`/`OrchestratorBridge`/`Workflow`), `Description`.

### 7.2 Bankaya Çıkış — `BankApiToolGateway`

MCP araçlarının bankaya çıkış noktası. Her araç bankayı **gövdeli tek bir POST** ile çağırır; gövde anahtarları bankanın sözleşmesindeki alan adlarının birebir aynısıdır.

- `AskBankAsync(bank, path, body, ct)`: **Banka hatası istisnaya dönüşmez** — bankanın kendi gerekçesi `error` alanında modele taşınır ve model kullanıcıya bunu anlatır. Bir aracın istisna fırlatması, uzmanın soruyu yanıtsız bırakması demektir.
- `Field(element, name)` / `Number(...)` / `Text(...)`: Büyük/küçük harf duyarsız JSON okuma yardımcıları.

### 7.3 Araç Sınıfları

Üçü de `[McpServerToolType]` + `[ToolGroup]` taşır ve **veriyi kendileri üretmez**; bankanın yanıtını olduğu gibi taşırlar.

- **`AccountsTools`** (`Account`) — `list_accounts`, `get_account_balance`, `get_transactions`.
- **`CardsTools`** (`Card`) — kart listesi, borç/limit, gecikmiş kartlar.
- **`AnalyzeTools`** (`Analyze`) — ekstre analizleri (`GetCreditCardStatement`, `AnalyzeStatementSummary`, `AnalyzeCategoryBreakdown`, `AnalyzePaymentTypeRatio`, `AnalyzeBonusAndPoints`, `AnalyzeTopMerchants`, `CompareSpendingPeriods`), kampanya/finansman listeleri (`GetCampaings`, `GetFinancingOptions`) ve **semantik arama** (`KampanyaVektorelAra`, `RecommendFinancing`).

Katman disiplininin **tek istisnası semantik aramanın kendisidir**: banka bir gömme ucu sunmadığı için vektör indeksi bu süreçte tutulur. İndeksin **verisi yine bankadan gelir**.

**`CustomerContext`** — İsteği yapan müşterinin kimliği; MCP araçları bu değeri bankaya gönderdikleri gövdeye yazar. Sahiplik denetimi bankanın kararıdır. Yapılandırmadan (`BankApi:DefaultCustomerId`) gelir ve **sessiz varsayılanı yoktur**: yanlış kimlik, hata vermeden başka birinin verisini göstermek demektir.

### 7.4 Semantik Arayıcılar — `Semantic/`

- **`SemanticCampaignSearcher`** — `EnsureCampaignIndexBuiltAsync` / `RebuildCampaignIndexFromBankAsync` bankadan (`/api/campaigns`) okur; `SearchCampaignsByIntent(intent, mccFilter, topK)` eşik `0.70`. `CampaignSummary` bankanın kayıt tipi değildir, yalnızca gerekli alanları taşır.
- **`SemanticFinancingSearcher`** — Aynı desen, `/api/financing-options`, eşik `0.65` (kullanıcı ihtiyacını ürün adıyla değil durumuyla anlatır, eşleşme metinsel olarak daha uzaktır).
- **`CampaignSyncService`** — `/api/campaigns/sync` ucunun arkasında; indeksi bankanın güncel hâline eşitler. **Yerel bir kampanya kataloğu tutmaz.**

Her ikisinde de tazeleme **kesintisizdir** (yeni indeks kurulduktan sonra tek adımda devreye alınır) ve banka yanıt vermezse **mevcut indekse dokunulmaz**.

---

## 8. `Banking.Domain` — Bankadan Bağımsız Ortak Parçalar

**Bu projenin hiçbir projeye referansı yoktur ve olmamalıdır.** Bankaya giden her yol `Banking.BankApi.Client` üzerinden HTTP'dir.

### 8.1 `SemanticIndex<T>` — tek vektör arama uygulaması

**`SemanticIndex<T>`** (`Semantic/SemanticIndex.cs`) — Yerel ONNX gömme modeliyle çalışan bellek içi anlamsal arama indeksi. Kayıt türünden bağımsızdır; aranan şey `textSelector` ve `similarityThreshold` ile belirlenir.

- `Replace(items)`: İndeksin tamamını değiştirir — **kesintisiz**, yeni içerik tamamen vektörleştirildikten sonra tek adımda devreye alınır.
- `AddOrUpdate(item)`: Tek kaydı ekler/günceller; çalışma zamanında eklenen kaydın anında görünmesini sağlayan yol.
- `Search(intent, topK, filter?)`: **Eleme sıralamadan ve kotadan önce yapılır** — önce `topK` alıp sonra eşik uygulamak, eşiği geçemeyen bir kaydın kotadan yer kapmasına ve eşiği geçen bir kaydın sonuçtan düşmesine yol açar.

Dört tüketici ve **bilerek farklı** eşikleri:

| Arayıcı | Eşik | Gerekçe |
|---|---|---|
| `SemanticWorkflowSearcher` | `0.5` | Sonuç karar değil, orkestratöre verilen **aday listesi**; yüksek eşik doğru akışı listeden tamamen düşürür |
| `SemanticMkCodeSearcher` | `0.70` | Sonuç kullanıcıyı doğrudan bir ekrana götürür; zayıf eşleşmeyle yanlış ekran açmak sonuçsuz kalmaktan kötüdür |
| `SemanticCampaignSearcher` | `0.70` | Sonuç kullanıcıya teklif olarak sunulur |
| `SemanticFinancingSearcher` | `0.65` | Kullanıcı durumunu anlatır, ürün adını değil |

### 8.2 Ekran Kodları ve Akış Metadatası

- **`MkCodeCatalog`** (`Banking.Mcp/Semantic/`) — Ekran kodu kataloğunun tek kaynağı; `POST /api/mkcodes` ile bankadan okunur. Sessiz varsayılan kod listesi yoktur: banka erişilemezken katalog boş kalır, bir sonraki çağrı yeniden dener. `MkCodeDefinition` (`Value`, `Label`, `Keywords`) taşıdığı kayıt tipidir.
- **`SemanticMkCodeSearcher`** — `EnsureScreenCodeIndexBuiltAsync()`/`RebuildScreenCodeIndexFromBankAsync()` kataloğu bankadan çekip `ISemanticIndexStore` üzerinden vektörize eder (içerik değişmediyse yeniden hesaplamaz); `SearchScreenCodesByIntentAsync` indeks boşsa kendini yeniden doldurur. Gömme metni etiket + kod + bankanın arama takma adlarını (`Keywords`) birleştirir. Varsayılan aday sayısı **10** — doğru kodun sıralamada üstte çıkması değil, listeye girmesi yeterlidir; hangisinin kastedildiğine model, cümleyi okuyarak karar verir.
- **`WorkflowMetadata`** (record) — `WorkflowName` + `Description`.
- **`SemanticWorkflowSearcher`** — `Initialize(workflows)`, `AddOrUpdate(workflow)` (çalışma zamanı kaydı), `Search(intent, topK)`.
- **`ISemanticIndexStore`** (`Banking.Domain/Semantic/`) — Hesaplanmış vektörlerin depolanacağı seam; anahtar indekslenen içeriğin özetidir, banka kataloğu değiştiğinde otomatik geçersizleşir. `InMemorySemanticIndexStore` tek örnekli çalışmada açılış maliyetini değiştirmez ama süreç ömrü boyunca tekrar tazelemede gömme hesabını atlar. `SmartComponents.LocalEmbeddings.EmbeddingF32` doğrudan serileştirilemez ama `Buffer` (`ReadOnlyMemory<byte>`) ↔ `.ctor(ReadOnlyMemory<byte>)` üzerinden kayıpsız gidip gelir — depo bunu saklar, `float[]`'i değil.

### 8.3 Sabitler

`ToolNames`, `TraceCategories`, `WorkflowPhases`, `WorkflowStatuses`, `AgentConstants` — araç adları, iz kategorileri, akış evreleri/durumları ve uzman adları tek yerde.

### 8.4 Ortak JSON Biçimi — `Serialization/JsonPolicy`

**`JsonPolicy`** (`Banking.Domain/Serialization/JsonPolicy.cs`) — Sistemin ürettiği tüm JSON'ın tek biçim kaynağı: araç sonuçları, ajanlar arası köprü yanıtları, iz yükleri (`TraceHub.JsonOptions`) ve HTTP gövdeleri hepsi `Readable`'ı paylaşır. `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` ile Türkçe karakterler ve tırnak işareti kaçışsız yazılır; girinti kapalıdır (token tasarrufu). `Banking.Agents/Runtime/AgentJsonPolicy.ToolOptions` aynı kodlayıcıyı C# ile yazılmış (`AIFunctionFactory`) araçların şema/sonuç serileştirmesine taşır — MAF'ın kendi varsayılan kodlayıcısı bu tek yoldan geri sızmasın diye.

---

## 9. Güvenlik Katmanları (Özet)

| # | Katman | Nerede |
|---|---|---|
| 1 | HITL onay kapısı — para hareketi model tarafından tetiklenemez | `DeterministicActionWorkflow`, `WorkflowEngine.ApplyApprovalDecisionAsync` |
| 2 | Idempotency — çift tıklama çift tahsilat üretmez | `GenericConfirmationView.ComputeIdempotencyToken` + oturum kilidi |
| 3 | Çıktı bariyeri — 6 kategoride sızıntı temizliği | `ResponseSanitizer` (tek çıkış noktası `GatewayService.FinalizeResponse`) |
| 4 | Oturum izolasyonu — farklı oturum iç içe açılamaz; oturum kimliği LLM aracına verilmez | `AmbientSessionAccessor` |
| 5 | Araç kapsamı — uzman yalnızca kendi grubunu görür | `AgentCard.ToolGroup`, `McpToolProvider` |
| 6 | Fail-fast araç doğrulaması — registry ile canlı MCP listesi ayrışırsa açılışta patlar | `ToolRegistry.ValidateDiscoveredMcpTools` |
| 7 | Şema kapısı — uymayan slot değeri oturuma yazılmaz | `SchemaValidatedSlotWriter` |
| 8 | Deklaratif çakışma koruması — deklaratif tanım derlenmiş uzmanı gölgeleyemez | `SpecialistCatalog.GuardAgainstCompiledCollision` |
| 9 | Müşteri kimliği — sessiz varsayılan yok | `CustomerContext` |

---

## 10. Yeni Bir Şey Eklerken

- **Yeni iş akışı:** `src/Banking.Host/declarative-agents/` altına bir YAML dosyası, ya da `POST /api/workflows/register-declarative`. **C# yazılmaz.** Para hareketi varsa `action.path` tanımlanmalıdır (onay kapısı buna bağlıdır).
- **Yeni MCP aracı:** `*Tools` sınıfına `[McpServerTool]` metodu; gerekiyorsa `[ToolGroup]`. `ServiceRegistration`'da `.WithTools<T>()` ile kaydedilir.
- **Yeni uzman:** `specialist-agents/` altına YAML, ya da statik `AgentCard Card` özelliği taşıyan bir sınıf. Uzman başına `Build` metodu yoktur.
- **Yeni prompt kuralı:** Yalnızca `AgentPrompts` içinde; alan adı/önek/örnek hardcode edilmez, genel ilke yazılır (bkz. `docs/PROMPT-YAZIM-REHBERI.md`).
- **Gerçek bankaya geçiş:** `appsettings.json` → `BankApi`, `BankApiPaths.cs`, akış YAML'larındaki `action.path`, `CampaignSummary`/`FinancingSummary` alan eşlemesi. Başka yere dokunulmaz.

---

## 11. Çalıştırma ve Doğrulama

```powershell
$env:OPENAI_API_KEY = "sk-..."
dotnet run --project src/Banking.BankApi   # vekil banka, port 5001
dotnet run --project src/Banking.Host      # port Agents:SelfBaseUrl'den gelir

# LLM'siz: HITL onay/ret/çift tıklama senaryoları
dotnet run --project tools/WorkflowTest

# Çalışan host'a karşı yolculuk/yük testi
dotnet run --project tools/LoadTest
```

**Tak-çalıştır kabul testi** — bankaya olan yapısal bağların kesik kaldığını doğrular:

```powershell
dotnet sln BankingAgents.sln remove src/Banking.BankApi/Banking.BankApi.csproj
dotnet build src/Banking.Host        # DERLENMELİ
dotnet sln BankingAgents.sln add src/Banking.BankApi/Banking.BankApi.csproj
```
