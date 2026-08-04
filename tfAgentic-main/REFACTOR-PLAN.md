
# Refactor Planı — Modüler, Stateless, Fintech Seviyesi Ajan Mimarisi

> Hazırlanma tarihi: 2026-08-04 · Dal: `refactor`
> Kapsam: ölü kod temizliği, mantık hatalarının giderilmesi, god class parçalama,
> oturum izolasyonu ve deklaratif (YAML) iş akışlarının gerçek aksiyon çalıştırması.
>
> Bu belge **yalnızca plandır**; kod değişikliği içermez.

---

## 0. Yönetici Özeti

Sistem çalışıyor ve mimari fikir doğru, ancak üç yapısal problem var:

1. **İki paralel motor.** `CollectionConversation` (elle yazılmış LLM döngüsü, aktif) ile
   `CollectionWorkflow` + `GenericSlotFillingStep` (MAF grafiği, tamamen ölü) aynı işi
   iddia ediyor. `CLAUDE.md`'nin 1 numaralı mimari maddesi **ölü olan sınıfı** anlatıyor.
2. **Ajanlar stateless değil.** `sessionId`, tool closure'larının içine gömülü olduğu için
   her oturumda yeni bir `AIAgent` kuruluyor; orkestratör ise tüm kullanıcılar için **tek
   bir `AgentSession`** paylaşıyor. Bu, çok kullanıcılı ortamda müşteri verisi sızıntısıdır.
3. **Jenerik iddia ile gerçek uyuşmuyor.** Sonuç işleme `PaymentOutcome`'a sabitlenmiş,
   deklaratif akışlar sahte makbuz üretiyor, `mkCode` eşlemesi kart ödemesine hardcoded.

Buna ek olarak, **doğrulama yanlış anda yapılıyor**: bakiye yeterliliği yalnızca onay
anında, veri katmanının içinde kontrol ediliyor. Kullanıcı tüm bilgileri verip onay ekranını
gördükten *sonra* "bakiye yetersiz" cevabı alıyor.

Hedef mimari üç ayağa dayanır:

1. **Stateless ajanlar.** Ajan nesneleri değişmez konfigürasyondur ve singleton'dır; kullanıcıya
   ait her şey `sessionId` anahtarlı store'da yaşar (`AIAgent.SerializeSessionAsync` ile konuşma
   geçmişi dahil). İzolasyon `AsyncLocal` accessor ile thread seviyesinde garanti edilir.
2. **Askıya alınmayan onay kapısı.** MAF workflow'u istekler arasında bellekte tutulmaz;
   onay geldiğinde tek istek içinde baştan sona koşar. MAF HITL kapısı korunur, sunucuda
   asılı nesne kalmaz. (MAF checkpoint'i değerlendirildi ve gereksiz bulundu — bkz. 2.2.)
3. **Erken doğrulama — kural bankada, çağrı bizde.** Her slot dolduğu anda bankanın
   ön-doğrulama (dry-run) servisi çağrılır ve cevabı kullanıcıya yansıtılır; hatayı onay
   ekranına ulaşmadan önce öğrenir. Bankacılık kuralları **bizim katmanımızda yazılmaz**;
   demo aşamasında `Banking.Domain` içinde gerçek backend'in mock'u olarak durur (bkz. 2.6).

---

## 1. Doğrulanmış Bulgular

Tüm bulgular kod okunarak ve `dotnet build` ile teyit edilmiştir.
Ana çözüm derlemesi **0 uyarı / 0 hata** verir — yani bu problemlerin hiçbirini derleyici yakalamaz.

### 1.1 KRİTİK — Yanlış sonuç veya para hareketi

| # | Yer | Problem |
|---|---|---|
| **K1** | `CollectionConversation.cs:187` | Başarı kontrolü `outcome is PaymentOutcome { Receipt: { } r }` şeklinde sabit. `TransferOutcome` bu desene uymaz → **başarılı para transferi kullanıcıya "İşlem sonlandırıldı" diye raporlanır**, oturum `CompleteSession` yerine `CancelSession` ile kapanır. |
| **K2** | `PaymentApprovalWorkflow.cs:56` | `CollectionService.Charge()` try/catch'siz çağrılıyor. `InMemoryBankStore.ApplyPayment` bakiye yetersizliğinde exception fırlatır → MAF `WorkflowOutputEvent` üretmez → `CollectionConversation.cs:202` devreye girer ve `approved` true olduğu için **başarısız ödemede "İşlem tamamlandı." mesajı** döner. |
| **K3** | `CollectionConversation.cs:205-211` | `ParseApproval` substring eşleştirmesi yapıyor. `"onaylamıyorum"` → `Contains("onayl")` → **onay sayılır**. `"kabul etmiyorum"` → `Contains("kabul")` → onay sayılır. Serbest metin yazan kullanıcıda para hareketi tetiklenir. |
| **K4** | `CollectionConversation.cs:170-203` | `_run` / `_events` (`IAsyncEnumerator`) üzerinde eşzamanlılık koruması yok. Kullanıcı "Onayla"ya çift tıklarsa iki thread aynı anda `SendResponseAsync` çağırır → **çift tahsilat riski**. |
| **K5** | `AgentRuntime.cs:123` | `_session ??= await _orchestrator!.CreateSessionAsync()` — singleton üzerinde **tüm kullanıcılar için tek sohbet oturumu**. B kullanıcısı A'nın konuşma geçmişini görür. Ayrıca sınırsız bellek büyümesi. |
| **K6** | `AgentRuntime.cs:51-52, 89-90, 565-566` | Singleton'da mutable `_currentSessionId` / `_currentMessage`. Eşzamanlı iki istekte A'nın `start_workflow` çağrısı B'nin `sessionId`'siyle akış başlatabilir. |
| **K7** | `TransferActionExecutor.cs:55-59` → `CollectionConversation.cs:196` | Transfer hatası `TransferCancelled(ex.Message)` içine konuyor ama üst katman bu mesajı yutup sabit "İşlem sonlandırıldı." dönüyor. Kullanıcı **neden başarısız olduğunu asla öğrenmiyor**. |
| **K8** | `WorkflowSessionStore.cs:272-275` | Tip dönüşümü `catch { args[i] = p.HasDefaultValue ? p.DefaultValue : null; }` ile sessizce yutuluyor. Tutar parse edilemezse `Amount` sessizce `0`/`null` olur. Fintech'te kabul edilemez. |
| **K10** | `Program.cs:112` ↔ `WorkflowRegistry.AutoDiscoverWorkflows` | *(Faz 1'de bulundu ve düzeltildi)* Keşif tek assembly tarıyordu: `typeof(PaymentSlots).Assembly` = **Banking.Domain**. Slot record'ları orada, ancak eksekütörler **Banking.Agents**'ta. Eksekütör taraması hiçbir tip bulamıyor → `_workflowConversationFactories` boş kalıyor → `CreateConversationAsync("card-payment")` `KeyNotFoundException` atıyor. Artık iki assembly de taranıyor. |
| **K9** | `DeclarativeWorkflowBuilder.cs:152-171` | `DeclarativeActionExecutor.ExecuteAsync` slotlara hiç bakmadan sabit sahte makbuz üretiyor (`"DECLARATIVE-ITEM"`, tutar `0m`, hesap `"ACC-1001"`). Kullanıcıya **"Ödeme başarıyla tamamlandı · Kalan Borç: 0,00 ₺"** deniyor — örneğin limit artırım talebi için. |

### 1.2 YÜKSEK — İşlevsiz özellik veya ölçek engeli

| # | Yer | Problem |
|---|---|---|
| **Y1** | `GatewayService.cs:74` ↔ `AgentRuntime.cs:607` | Gateway `TryGetValue("mkCode", ...)` arıyor, tool parametresi ise `targetMkCode`. Eşleşme hiç olmaz → **ekran yönlendirme (OPEN_SCREEN) uçtan uca hiç çalışmıyor.** |
| **Y2** | `GatewayService.cs:140` | `MkCodesRegistry.Get(confirmation.WorkflowName)` — `WorkflowName` değeri `"TransferSlots"` (tip adı), registry'de yok → fallback `"CreditCardPayment"`. **Transfer onay ekranı kredi kartı ödemesi olarak render ediliyor.** |
| **Y3** | `AgentRuntime.cs:297-331` | `topMatchedWorkflows != null` olan **her istek** orkestratörü baştan kuruyor ve global `_orchLock` semaphore'unda sıraya giriyor. Semantik eşleşme neredeyse her istekte olduğundan tüm trafik tek kilitte serileşiyor. |
| **Y4** | `WorkflowRegistry.cs:18` | `_workflowConversationFactories` düz `Dictionary`. `/api/workflows/register-declarative` runtime'da yazarken `CreateConversationAsync` eşzamanlı okuyor → thread-safe değil (bozulma / sonsuz döngü riski). |
| **Y5** | `WorkflowRegistry.cs:21, 48, 84` | `_discoveredWorkflows` düz `List`, tekilleştirme yok. Aynı YAML tekrar POST edilince RAG indeksine **çift kayıt** girer, semantik yönlendirme bozulur. |
| **Y6** | `WorkflowRegistry.cs:88, 170` | `_rootServiceProvider.CreateScope()` hiç `Dispose` edilmiyor → her akış başlatmada **DI scope sızıntısı**. |
| **Y7** | `WorkflowSessionStore._sessions`, `GatewayService._sessions`, `WorkflowConversationStore._activeWorkflowConversations` | Hiçbirinde tahliye/TTL yok. Her yarım kalan oturum ayrıca bir `AIAgent` + `AgentSession` + `StreamingRun` + `IAsyncEnumerator` tutuyor → **sınırsız bellek büyümesi**. |
| **Y8** | `AgentRuntime.cs:441-505` | `sessionId`, `update_slot` / `propose_X` / `cancel_X` closure'larına gömülü. Bu yüzden **her oturum için yeni bir `AIAgent`** (+~30 MCP tool sarmalayıcısı) kuruluyor. Ajanların stateless olamamasının kök nedeni budur. |
| **Y9** | `ToolRegistry.cs:107` | `ValidateDiscoveredMcpTools` **hiçbir yerden çağrılmıyor**. `CLAUDE.md`'de "startup fail-fast güvenlik kontrolü" olarak belgeli; gerçekte yok. |
| **Y10** | `Program.cs:217` | `Path.Combine(agentsFolder, $"{definition.Name.ToLowerInvariant()}.yaml")` — `name` doğrudan dosya yoluna giriyor. `name: ../../appsettings` ile **dizin dışına dosya yazılabilir** (path traversal). |
| **Y11** | `CollectionConversation.cs:126-131` | Konu dışı algılama Türkçe string eşleştirmesiyle (`"bilgi veremiyorum"` vb.) — çok kırılgan. Ayrıca `AskOrchestratorAsync(userMessage)` **sessionId'siz** çağrılıyor, bu da K6'daki singleton alanını bozuyor. Üstüne yanıltıcı bir `SECURITY` trace'i basıyor ("iç bilgi sızıntısı tespit edildi") — gerçek güvenlik olaylarını gürültüye boğar. |
| **Y12** | `WorkflowSessionStore.cs:123, 233` | `typeof(TSlots) == typeof(PaymentSlots)` ve `"Date"` string'i hardcoded. "Tamamen jenerik" iddiasını doğrudan ihlal ediyor. |
| **Y13** | `WorkflowRegistry.cs:96-106` ↔ `CollectionConversation.cs:105` | Deklaratif akışta `PendingSlots` başlangıçta **tüm değerleri null olan şema şablonu** ile dolduruluyor. `PendingSlots is TSlots` kontrolü `Dictionary<string,object?>` için ilk andan itibaren true. Kullanıcı slotları `update_slot` ile doldurup LLM `propose_X` çağırmazsa, **null'lu şablon onay ekranına gider**. |
| **Y14** | `AnalyzeTools.cs:27, 34, 190-199, 254-256` | **Katman ihlali:** MCP aracı `InMemoryBankStore`'u doğrudan enjekte edip kullanıyor (`GetCampaings`, `GetFinancingOptions`). `AccountsTools` ve `CardsTools` doğru şekilde servis üzerinden gidiyor; yalnızca bu sınıf veri katmanına iniyor. Ayrıca store `null` olabildiği için sessizce "veritabanına ulaşılamıyor" dönüyor — sıfır hata toleransı ilkesine aykırı sessiz bozulma. |
| **Y15** | `InMemoryBankStore.cs:65-70, 88-92` | **Doğrulama yalnızca işlem anında var.** Bakiye yeterliliği, tutar pozitifliği ve "tutar borçtan fazla olamaz" kontrolleri sadece para hareketi sırasında, exception olarak çalışıyor. Ön-doğrulama (dry-run) yolu yok → kullanıcı hatayı ancak "Onayla"ya bastıktan sonra öğreniyor. *Bu kurallar bankaya aittir ve bankada kalır; eksik olan, bankanın bunları önceden sorulabilir hale getirmesidir (bkz. 2.6 mock).* |
| **Y16** | `TransferService.cs:17-24` → `InMemoryBankStore.cs:82` | **IBAN doğrulaması hiç yok.** `targetIban` herhangi bir string olabilir; format/checksum kontrolü yapılmıyor. Hedef bankamızda bulunamazsa para sessizce hesaptan çıkıyor (EFT simülasyonu). Yanlış yazılmış bir IBAN sessiz para kaybına yol açar. *Bankanın kuralıdır → mock ön-doğrulama içine eklenir, ajan katmanına değil.* |

### 1.3 ORTA — Ölü kod ve tutarsızlık

| # | Yer | Problem |
|---|---|---|
| **O1** | `Collection/CollectionWorkflow.cs` (105 satır) | `CollectionWorkflow.Build` ve `GenericSlotFillingStep<TSlots>` **hiçbir yerden çağrılmıyor**. `CLAUDE.md` madde 1 bu ölü sınıfı sistemin merkezi olarak anlatıyor. |
| **O2** | `Campaigns/CampaignsRegistry.cs` + `CampaignsDefinition.cs` (162 satır) | Tamamen ölü. |
| **O3** | `MkCodes/ScreenCommand.cs` (29 satır) | Ölü. |
| **O4** | `AnalyzeTools.cs:271-288` | `AsTools()` ölü (araçlar MCP üzerinden geliyor). |
| **O5** | `AgentRuntime.cs:150-173` | `AskStreamingAsync` ölü. |
| **O6** | `Specialists/CustomerSupportAgent.cs` | Reflection ile keşfedilip kaydediliyor ama hiç kullanılmıyor. `ToolGroup.Orchestrator` verildiği için `ToolsForGroup` boş liste döndürürdü — yani kurulsa bile araçsız kalırdı. |
| **O7** | `DeterministicActionWorkflow.cs:10` | `IWorkflowExecutorMarker` işlevsiz marker; hiçbir tarama kullanmıyor. |
| **O8** | `IToolRegistry.cs:29-30` | `ToolDefinition.RequiresApproval` ve `AdditionalLeakPatterns` hiç okunmuyor. |
| **O9** | Çeşitli | Kullanılmayan sabitler: `ToolGroup.Collection`, `WorkflowScopes.Session`, `AgentConstants.WorkflowNames`, `TransferSlotKeys`. |
| **O10** | `ResponseSanitizer.cs:47, 183` | İki constructor; ikincisi gereksiz (DI zaten opsiyonel parametreliyi seçer). |
| **O11** | `src/Banking.Domain/Tracing/` | Boş klasör. |
| **O12** | `WorkflowPhases.cs` | 8 sabit, iki farklı kavram karışmış: `slot/confirm/done` (CollectionConversation) ve `idle/slot_filling/awaiting_approval/completed/cancelled` (SessionBag). |
| **O13** | `tools/WorkflowTest` | **Derlenmiyor** — `Banking.Agents.Collection` ve `Banking.Agents.Records` namespace'leri yok (doğrusu `Banking.Domain.Records`). Ayrıca `BankingAgents.sln`'e dahil değil. `CLAUDE.md` bunu çalışan komut olarak belgeliyor. |
| **O14** | `WorkflowConversationStore.cs:41-71` | 3 overload; biri `PaymentSlotKeys.WorkflowName`'e hardcoded. `/api/collect/start` ve `/api/collect/respond` endpoint'leri eski, UI kullanmıyor. |
| **O15** | `GatewayResponseType.CONFIRM_PAYMENT` + `GatewayService.cs:124` | Tüm iş akışları için "Ödeme başarıyla tamamlandı / Kalan Borç" sözcükleri. |
| **O16** | `AgentRuntime.cs:234-237` | `GetSpecialist` çift yönlü `Contains` ile bulanık eşleştirme yapıyor → yanlış uzmana yönlenme riski. |

---

## 2. Hedef Mimari

### 2.1 Temel ilke: durum sessionId anahtarlı store'da, ajanlar singleton

```
                 ┌─────────────────────────────────────────────┐
   HTTP isteği ─►│  IWorkflowSessionAccessor  (AsyncLocal)      │  ← sessionId ambient
                 └──────────────────┬──────────────────────────┘
                                    │
     ┌──────────────────────────────┼──────────────────────────────┐
     │                              │                              │
┌────▼─────────────┐   ┌────────────▼────────────────────────────────────────┐
│ Singleton ajanlar│   │ ISessionStateStore  (şimdi RAM, prod öncesi Redis)   │
│ (stateless)      │   │   StateBag[sessionId]                               │
│ Orkestratör      │   │     · slotlar                                       │
│ Uzmanlar         │   │     · faz + idempotency anahtarı                    │
│ Workflow ajanı   │   │     · serileştirilmiş AgentSession                  │
│                  │   │   kayan TTL (~1 dk) → kopan oturum kurtarma penceresi│
└──────────────────┘   └─────────────────────────────────────────────────────┘
```

Üç kural:

1. **Ajan nesnesi asla kullanıcıya özel veri taşımaz.** Değişmez konfigürasyondur (instructions + tools), uygulama ömrü boyunca tek örnek.
2. **Kullanıcıya özel her şey `sessionId` ile anahtarlanır.** Slotlar → StateBag; konuşma geçmişi → `AIAgent.SerializeSessionAsync` çıktısı. İstekler arasında bellekte canlı nesne tutulmaz.
3. **`sessionId` LLM'e asla parametre olarak verilmez.** `AsyncLocal` ambient scope üzerinden okunur — model uydurup başka oturuma erişemez.

### 2.2 Onay kapısı: workflow istekler arasında askıya alınmaz (Seçenek C — karar verildi)

MAF checkpoint'i **değerlendirildi ve reddedildi.** Gerekçe: onay grafiği yalnızca 2 düğüm
(`RequestPort → Executor`) ve istekler arasında taşıdığı tek durum (onay verisi, `RequestId`,
slotlar) zaten StateBag'de mevcut. Checkpoint bu veriyi ikinci kez serileştirmekten öteye
geçmezdi; buna karşılık serileştirme kısıtları, `resume` yolu ve ~1 günlük doğrulama spike'ı
maliyeti getirirdi. Checkpoint'in kazandığı senaryolar (çok adımlı grafik, gün/hafta süren
bekleme, MAF checkpoint geçmişinin resmî denetim izi olması) bu sistemde yok.

Bunun yerine: **MAF workflow'u hiç askıya alınmaz, tek istek içinde baştan sona koşar.**

**İstek N — slotlar tamamlandı:**
```
onay görünümünü StateBag'den kur
StateBag'e yaz: phase = awaiting_approval, slotlar, idempotency anahtarı
CONFIRM_PAYMENT dön          ← MAF run'ı HİÇ başlatılmaz
```

**İstek N+1 — kullanıcı onayladı:**
```
StateBag'den slotları oku (TTL dolmuşsa: dürüst "oturum zaman aşımı" mesajı)
workflow'u tek istek içinde koş: RequestPort'a onayı hemen ver → Executor → çıktı
WorkflowOutputEvent / WorkflowErrorEvent / ExecutorFailedEvent → hepsi işlenir
```

Kazanımlar: istekler arasında bellekte **hiçbir şey** kalmaz; sticky session gerekmez (herhangi
bir instance herhangi bir isteği karşılar); TTL doğal olarak store anahtarının TTL'i olur;
deploy/restart TTL içindeki akışları öldürmez; tek istek = tek stack trace olduğu için debug
en kolay haldedir.

MAF HITL kapısı **korunur**: `approved: bool` değerinin deterministik yola girdiği tek, tipli,
izlenebilir nokta olmayı sürdürür. LLM onay kararına hiç karışmaz — UI sabit token gönderir.

> **Zorunlu kural:** MAF Executor içindeki exception'lar yukarı fırlamaz, `WorkflowErrorEvent` /
> `ExecutorFailedEvent` olarak event akışına düşer. K2 hatasının kökü budur. Event akışını
> dinleyen **her** döngü bu iki event'i açıkça işlemek zorundadır.

> **Idempotency:** Onay isteği `sessionId + slot hash` anahtarıyla korunur; oturum başına
> `SemaphoreSlim` ile birlikte çift tıklamada çift tahsilat engellenir (K4).

### 2.3 Sınıf ayrıştırması (god class → SRP)

**`AgentRuntime` (574 satır) → 6 sınıf:**

| Yeni sınıf | Sorumluluk |
|---|---|
| `McpConnection` | MCP client yaşam döngüsü, `ToolsForGroup`, tracing sarmalama |
| `SpecialistCatalog` | Uzman keşfi (reflection), A2A ↔ in-process seçimi ve fallback |
| `OrchestratorAgentProvider` | Orkestratör ajanını **bir kez** kurar (RAG bağlamı prompt yerine mesaj olarak geçer) |
| `WorkflowAgentFactory` | `TSlots` için workflow ajanı üretimi (artık sessionId'siz → singleton'lanabilir) |
| `NavigationTools` | `ekran_kodu_ara` / `ekran_ac` araçları (Y1 burada düzelir) |
| `AgentRuntime` | Sadece facade + `GetDiagnosticsAsync` |

**`CollectionConversation` (184 satır) → 3 sınıf:**

| Yeni sınıf | Sorumluluk |
|---|---|
| `SlotFillingLoop` | LLM turu, slot durumu değerlendirmesi, `PROPOSAL_READY` ayrıştırma |
| `ApprovalGate` | Onay anında tek seferde koşan MAF RequestPort akışı + tüm event'lerin işlenmesi (hata event'leri dahil) |
| `WorkflowConversation` | İkisini sıralayan ince koordinatör + oturum başına `SemaphoreSlim` (K4) |

**`GatewayService` (154 satır) → 2 sınıf:** protokol haritalama (`GatewayResponseMapper`) ile
oturum yönetimi (`UserSessionStore`, TTL'li) ayrılır.

### 2.4 Jenerik sonuç modeli (`PaymentOutcome` bağımlılığını kaldırır)

```
WorkflowOutcome
  ├─ bool     IsSuccess
  ├─ string?  FailureReason          ← K7: hata sebebi artık kullanıcıya ulaşır
  ├─ string?  ReceiptId
  └─ IReadOnlyDictionary<string,object?> Details   ← akışa özel alanlar
```

Bu tek değişiklik K1, K7, K9 ve O15'i birlikte çözer ve deklaratif aksiyon bağlamanın ön koşuludur.

### 2.5 Deklaratif YAML aksiyon bağlama

YAML'a opsiyonel `action:` bloğu eklenir; **mevcut ve doğrulanmış MCP araç yüzeyine** bağlanır —
yeni bir çalıştırma yolu veya güvenlik yüzeyi açılmaz.

```yaml
name: limit-artirim
description: Kredi kartı limit artırım talebi toplar.
inputSchema:
  properties:
    cardId:         { type: string, description: "Kartın ID'si" }
    requestedLimit: { type: number, description: "Talep edilen yeni limit" }
action:
  kind: mcpTool               # none | mcpTool
  tool: request_limit_increase
  arguments:
    cardId:   "{{cardId}}"    # slot → araç parametresi eşlemesi
    newLimit: "{{requestedLimit}}"
  requiresApproval: true
```

`DeclarativeActionExecutor` şunu yapar: `IToolRegistry`'den aracı çöz → slotları argümanlara
eşle (tip doğrulaması ile, **sessizce null'a düşmeden**) → MCP üzerinden çağır → `WorkflowOutcome` dön.
`action.kind: none` ise dürüst bir "talep alındı" sonucu döner — sahte makbuz tamamen kalkar.

Doğrulama: `action.tool` **kayıtlı** bir MCP aracı olmalı; değilse YAML kaydı reddedilir
(fail-fast, kayıt anında — çalışma anında değil).

### 2.6 Erken doğrulama — kural bankada, çağrı bizde

**Problem:** Bugün doğrulamanın tamamı `InMemoryBankStore.ApplyPayment` içinde, yani **onay
anında** yapılıyor. Kullanıcı tüm slotları dolduruyor, onay ekranını görüyor, "Onayla"ya
basıyor ve ancak o an "Hesap bakiyesi yetersiz" cevabını alıyor. Bilgi en baştan biliniyordu.

**İlke: bankacılık kurallarını biz yazmayız.** Bakiye yeterliliği, limit uygunluğu, IBAN
geçerliliği — hepsi gerçek banka backend'inin sorumluluğudur. Bizim sistemimiz bankanın
**ön-doğrulama (dry-run) servisini çağırır** ve cevabını kullanıcıya yansıtır.

Sorumluluk yalnızca ikiye ayrılır:

| Kim | Ne | Nerede |
|---|---|---|
| **Biz** | Tip/ayrıştırma: LLM'in ürettiği `"onbin"` değeri `decimal`'e çevrilebiliyor mu? Bankacılık kuralı değil, LLM çıktısı temizliğidir. | Faz 1 (K8) |
| **Banka** | Bakiye, limit, borç, IBAN geçerliliği — **tüm iş kuralları** | Banka servisi; cevabı yüzeye çıkarılır |

**Mock (demo aşaması).** Gerçek backend'in sunacağı ön-doğrulama çağrısı, **simüle edilen banka
tarafında** (`Banking.Domain`) taklit edilir. Ajan katmanına hiçbir kural sızmaz.

```csharp
// Banking.Domain/Validation — bankanın döndüreceği cevabın şekli
public sealed record BankValidationResult(
    bool IsAcceptable,
    string? RejectionCode,      // "INSUFFICIENT_BALANCE", "INVALID_IBAN", "EXCEEDS_DEBT" ...
    string? UserMessage,        // kullanıcıya gösterilebilir, banka tarafından üretilmiş mesaj
    IReadOnlyDictionary<string, object?>? Hints);   // örn. { "maxAllowedAmount": 12500 }

// Simüle edilen banka servisleri — gerçek dry-run endpoint'inin mock'u.
// DİKKAT: parametreler nullable, çünkü slotlar KISMİ doluyken de çağrılır.
public interface ICollectionService
{
    BankValidationResult ValidatePayment(string? cardId, decimal? amount, string? sourceAccountId);
    // ... mevcut üyeler
}

public interface ITransferService
{
    BankValidationResult ValidateTransfer(string? sourceAccountId, string? targetIban, decimal? amount);
    // ... mevcut üyeler
}
```

Mock, yalnızca **elindeki bilgiyle** karar verir: eksik parametre varsa o kuralı atlar,
`IsAcceptable = true` döner. Böylece slotlar doldukça kademeli olarak daha çok kural devreye girer.

**Ajan katmanındaki adapter — sıfır kural, yalnızca eşleme:**

```csharp
// Banking.Agents — TSlots yalnızca keşif/kayıt için; veri KISMİ bag olarak gelir
public interface ISlotValidator<TSlots>
{
    ValueTask<BankValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, object?> filledSlots, CancellationToken ct);
}

public sealed class PaymentSlotValidator(ICollectionService collections) : ISlotValidator<PaymentSlots>
{
    public ValueTask<BankValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, object?> slots, CancellationToken ct)
        => ValueTask.FromResult(collections.ValidatePayment(
            cardId:          slots.GetValueOrDefault(nameof(PaymentSlots.CardId)) as string,
            amount:          slots.GetValueOrDefault(nameof(PaymentSlots.Amount)) as decimal?,
            sourceAccountId: slots.GetValueOrDefault(nameof(PaymentSlots.SourceAccountId)) as string));
}
```

Gerçek backend geldiğinde `ICollectionService.ValidatePayment` bir HTTP çağrısına dönüşür;
adapter ve üstündeki hiçbir şey değişmez.

**Choke point olarak `update_slot`.** Doğrulama, mevcut MAF tool mekanizmasının içine oturur:

```
update_slot("Amount", 15000)
  └─ ISlotValidator.ValidateAsync(kısmi slot bag'i)   → banka çağrısı
       ├─ IsAcceptable        → StateBag'e yaz, "kaydedildi" dön
       └─ IsAcceptable = false → slotu YAZMA, tool sonucu olarak bankanın mesajını dön:
                                 "Hesap bakiyeniz 12.500 ₺, bu tutarı karşılamıyor."
                                 (+ Hints varsa kabul edilebilir aralık eklenir)
```

LLM bu tool sonucunu görüp kullanıcıya doğal Türkçe ile yeniden sorar. **Karar bankanındır**,
LLM yalnızca ifade eder. Yeni bir konuşma mekanizması eklemeye gerek yok.

**Keşif:** `ISlotValidator<TSlots>` uygulamaları, `IDeterministicActionExecutor<,,>` ile aynı
şekilde `WorkflowRegistry` tarafından reflection ile otomatik keşfedilir. Validator tanımlanmamış
bir akış doğrulamasız çalışır (mevcut davranış korunur) — projenin attribute/reflection
felsefesiyle tutarlı.

**İşlem anı güvencesi korunur.** `Charge`/`Transfer` içindeki atomik kontroller yerinde kalır
(banka tarafı). Ön-doğrulamadan bu yana bakiye değişmiş olabilir; o durumda reddetmek doğru
davranıştır ve artık kullanıcının *ilk* duyduğu haber değildir.

### 2.7 Katman disiplini (zorunlu kural)

```
Banking.Mcp (araçlar)  ──►  Banking.Domain servisleri (saf C#, tüm iş kuralları)  ──►  store
     ✗ store'a doğrudan erişim YASAK              ✓ tek doğrulama ve kural noktası
```

- **MCP araçları asla veri katmanına dokunmaz.** Yalnızca `I*Service` arayüzleri üzerinden gider.
  Bugün `AccountsTools` ve `CardsTools` uyumlu; `AnalyzeTools` ihlal ediyor (Y14) — `ICampaignService`
  ve `IFinancingService` eklenerek düzeltilir.
- **İş kuralları servis katmanında, saf C# olarak.** `InMemoryBankStore` yalnızca kalıcılık
  yapar; bakiye/limit/tutar kuralları servise taşınır (Y15).
- **Sessiz bozulma yasak.** Opsiyonel-null bağımlılık + "ulaşılamıyor" fallback kalıbı kaldırılır;
  eksik bağımlılık başlangıçta fail-fast verir.
- **Sessiz tip dönüşümü yasak.** Başarısız dönüşüm slotu "eksik" işaretler (K8), asla `0`/`null`'a düşmez.

---

## 3. Fazlar

Fazlar sıralıdır; her faz sonunda sistem çalışır durumdadır.

### Faz 0 — Test zemini (~yarım gün)

**Neden:** Otomatik test yok; sonraki her fazın tek doğrulama aracı bu.

- `tools/WorkflowTest` onarılır (O13: `Banking.Agents.Collection` / `Banking.Agents.Records`
  → doğrusu `Banking.Domain.Records`) ve `BankingAgents.sln`'e eklenir.
- Senaryolar genişletilir: onay, red, **yetersiz bakiye** (K2 için), **başarılı transfer** (K1 için).
- Bu senaryolar başta **kırmızı** olmalı — K1 ve K2'nin varlığını kanıtlarlar.

> Checkpoint spike'ı **iptal edildi** (bkz. 2.2). Seçenek C askıya alma gerektirmediği için
> doğrulanacak riskli bir MAF yolu kalmadı; ~1 günlük spike planı düşürüldü.

### Faz 1 — Kritik doğruluk hataları ✅ TAMAMLANDI

Kapsam: **K1, K2, K3, K7, K8, K10, Y1, Y2** + `WorkflowOutcome` modeli (2.4).
Ek olarak: `IDeterministicActionExecutor` 3 tip parametresinden **1'e** indirildi (`TConfirmation`
ve `TOutcome` her uygulamada aynıydı — sahte jeneriklik), ölü `CollectionWorkflow.cs` ve
`WorkflowScopes.cs` silindi (Faz 4'ten öne alındı), `WorkflowRegistry` içindeki DI scope sızıntısı
ve çift RAG kaydı giderildi (Y5, Y6).

- `WorkflowOutcome` tanıtılır; `PaymentOutcome`/`TransferOutcome` ona göre uyarlanır → K1.
- Tüm `IDeterministicActionExecutor` uygulamalarında domain exception'ları yakalanır ve
  `WorkflowOutcome.Failure(reason)`'a çevrilir → K2, K7.
- `ParseApproval` yerine **açık olumsuzluk önceliği** olan bir çözümleyici; UI zaten sabit
  `onayla`/`iptal` gönderiyor, serbest metin için olumsuz kalıplar (`-ma/-me`, `değil`, `yapma`)
  önce değerlendirilir. Belirsizse **onay sayılmaz**, tekrar sorulur → K3.
- Slot tip dönüşümü: başarısız dönüşüm artık sessizce null'a düşmez; slot "eksik" işaretlenip
  kullanıcıya tekrar sorulur → K8.
- `ekran_ac` parametre adı ile gateway'in aradığı anahtar tek bir sabitte birleştirilir → Y1.
- Onay ekranı `mkCode` çözümü `[Workflow("...")]` adı üzerinden yapılır (tip adı değil),
  fallback `"CreditCardPayment"` kaldırılır → Y2.

**Doğrulama:** Faz 0'da kırmızı olan senaryolar yeşile döner.

### Faz 2 — Katman disiplini ve erken doğrulama ✅ TAMAMLANDI

Kapsam: **Y14, Y15, Y16** + 2.6 ve 2.7'deki tasarım.
Ek olarak: seed IBAN'ları mod-97 geçerli değerlerle değiştirildi (eskiler 24 karakterdi ve
kontrol basamağı tutmuyordu — bankanın kendi doğrulaması demo hesaplarını reddederdi).
`Charge`/`Transfer` yürütme anında da aynı ön-doğrulama metodunu çağırır: kural çiftlenmesi yok.
**Amaç:** Faz 1 hataları dürüst hale getirdi; bu faz onları **erken** hale getirir.

**Simüle edilen banka tarafı (`Banking.Domain`) — gerçek backend'in mock'u:**
- `BankValidationResult` kaydı eklenir.
- `ICollectionService.ValidatePayment` ve `ITransferService.ValidateTransfer` eklenir.
  Nullable parametrelerle çalışır: eksik bilgi olan kuralı atlar, elindekiyle karar verir.
- Mock içeriği mevcut kuralları yansıtır (bakiye, borç, tutar pozitifliği) **artı** IBAN
  biçim/checksum kontrolü — bunlar bankanın kuralları, bizim değil (eski Y16 buraya taşındı).
- `Hints` ile kabul edilebilir aralık döner (örn. `maxAllowedAmount`).

**Ajan tarafı (`Banking.Agents`) — sıfır kural:**
- `ISlotValidator<TSlots>` + `PaymentSlotValidator` / `TransferSlotValidator` — her biri tek
  satırlık delegasyon. `WorkflowRegistry` reflection ile keşfeder.
- `update_slot` validator'dan geçirilir: banka reddederse slot StateBag'e **yazılmaz**,
  bankanın mesajı tool sonucu olarak LLM'e döner.

**Katman ihlali (Y14):**
- `ICampaignService` + `IFinancingService` eklenir; `AnalyzeTools`'un `InMemoryBankStore`
  bağımlılığı kesilir. Opsiyonel-null + "ulaşılamıyor" fallback kalıbı kaldırılır, eksik
  bağımlılık başlangıçta fail-fast verir.

**Doğrulama:** Kullanıcı bakiyesinden büyük tutar girdiğinde **onay ekranına hiç ulaşmadan**,
tutarı verdiği anda bankanın mesajını alır. Geçersiz IBAN aynı şekilde anında reddedilir.
Ajan katmanında hiçbir bankacılık karşılaştırması bulunmadığı kod incelemesiyle doğrulanır.

### Faz 3 — Oturum izolasyonu ve stateless ajanlar ✅ TAMAMLANDI

Kapsam: **K4, K5, K6, Y3, Y7, Y8, Y11** + 2.1 ve 2.2'deki tasarım.
Ek olarak: `WorkflowPhases` sabitleri tekilleştirildi (`slot`/`confirm`/`done` ikizleri kaldırıldı),
kullanılmayan `WorkflowRegistry.Register<TSlots>` ve ölü `AskStreamingAsync` silindi,
`GatewayService._sessions` sözlüğü TTL'li `IMemoryCache`'e taşındı.

- `IWorkflowSessionAccessor` (AsyncLocal) eklenir; gateway her istekte `sessionId` scope'u açar.
- `update_slot` / `propose_X` / `cancel_X` araçları `sessionId`'yi closure yerine accessor'dan
  okur → workflow ajanı **singleton**'a dönüşür (Y8).
- Orkestratör oturumu: paylaşılan `_session` kaldırılır. Oturum başına `AgentSession`,
  `SerializeSessionAsync` ile StateBag'e yazılır, sonraki istekte `DeserializeSessionAsync`
  ile geri yüklenir → K5.
- `_currentSessionId` / `_currentMessage` alanları silinir; ilgili değerler accessor'dan gelir → K6.
- Orkestratör bir kez kurulur; RAG eşleşmeleri prompt'a gömülmek yerine **çalışma anında mesaj
  olarak** geçirilir → rebuild ve global kilit ortadan kalkar (Y3).
- `WorkflowConversation` başına `SemaphoreSlim` + `sessionId + slot hash` idempotency anahtarı
  → aynı oturumda eşzamanlı istek serileşir, çift tıklama çift tahsilat üretmez (K4).
- Onay kapısı 2.2'deki **Seçenek C**'ye geçirilir: `StreamingRun` artık istekler arasında
  tutulmaz, workflow onay isteğinde tek seferde koşar.
- `ISessionStateStore` soyutlaması eklenir (slotlar + serileştirilmiş `AgentSession`),
  kayan TTL ile. Implementasyon şimdilik RAM; prod öncesi Redis tek DI satırıyla takılır.
- Y11'deki konu dışı fallback'i: sessionId artık her zaman doğru geçer; yanıltıcı `SECURITY`
  trace'i `Workflow` kategorisine taşınır.

**Doğrulama:** İki tarayıcı oturumuyla eşzamanlı akış; A ve B'nin slotlarının ve geçmişinin
karışmadığı, çift tıklamanın çift tahsilat üretmediği doğrulanır. TTL sonrası dönen kullanıcı
"oturum zaman aşımı" mesajı alır (sessizce yanlış davranış değil).

### Faz 4 — Ölü kod temizliği

Kapsam: **O1–O11**. Yaklaşık **380+ satır** siliniyor, davranış değişmiyor.

`CollectionWorkflow.cs`, `CampaignsRegistry` + `CampaignsDefinition`, `ScreenCommand`,
`AnalyzeTools.AsTools()`, `AskStreamingAsync`, `CustomerSupportAgent`, `IWorkflowExecutorMarker`,
kullanılmayan `ToolDefinition` alanları ve sabitler, `ResponseSanitizer`'ın fazla ctor'u,
boş `Banking.Domain/Tracing` klasörü.

> Ölü kod temizliğinin **Faz 3'ten sonra** gelmesi bilinçli: `CollectionWorkflow` içindeki
> `GenericSlotFillingStep`, Faz 3'teki onay kapısı dönüşümünde referans olarak işe yarayabilir.

### Faz 5 — God class parçalama

Kapsam: 2.3'teki ayrıştırma + **O12, O14, O16**.

- `AgentRuntime` → 6 sınıf; `CollectionConversation` → 3 sınıf; `GatewayService` → 2 sınıf.
- `WorkflowPhases` iki ayrı enum'a bölünür (konuşma fazı ↔ oturum durumu) → O12.
- `WorkflowConversationStore` overload'ları tek imzaya indirilir; `/api/collect/*` endpoint'leri
  kaldırılır → O14.
- `GetSpecialist` bulanık eşleştirmesi tam eşleşmeye çevrilir (bulunamazsa net hata) → O16.

**Kısıt:** Bu faz tamamen davranış korumalıdır. Faz 1–4 bittikten sonra yapılır ki her adım
`WorkflowTest` ve elle senaryolarla doğrulanabilsin.

### Faz 6 — Deklaratif YAML aksiyon bağlama

Kapsam: **K9** + 2.5'teki tasarım.

- `MicrosoftAgentDeclarativeDefinition`'a `Action` bloğu eklenir.
- `DeclarativeActionExecutor` gerçek MCP aracı çağırır; sahte makbuz silinir.
- Kayıt anında doğrulama: `action.tool` `IToolRegistry`'de yoksa YAML reddedilir.
- Slot → argüman eşlemesinde tip doğrulaması (Faz 1'deki dönüşüm kurallarıyla aynı).

**Doğrulama:** `limit-artirim.yaml` gerçek bir MCP aracına bağlanıp uçtan uca koşulur.

### Faz 7 — Dayanıklılık, tutarlılık ve doküman

Kapsam: **Y4, Y5, Y6, Y7, Y9, Y10, Y12, Y13, O15**.

- `WorkflowRegistry`: `ConcurrentDictionary` + isim bazlı tekilleştirme + scope'ların
  düzgün dispose edilmesi → Y4, Y5, Y6.
- Store'lara TTL/tahliye (kayan zaman aşımı + arka plan temizlik) → Y7.
- `ValidateDiscoveredMcpTools` gerçekten startup'ta çağrılır (`CLAUDE.md`'deki iddia karşılanır) → Y9.
- Deklaratif ajan dosya adı sanitizasyonu (yalnızca `[a-z0-9-]`, path ayracı yasak) → Y10.
  *Not: auth iç ağ nedeniyle kapsam dışı, ayrıca ele alınacak.*
- `WorkflowSessionStore`'daki `PaymentSlots` / `"Date"` özel durumu, slot record'unda
  `[DefaultValue]` benzeri bir attribute ile jenerikleştirilir → Y12.
- Deklaratif akışta `PendingSlots` başlangıç şablonu kaldırılır; "tamamlandı" kararı
  yalnızca gerçekten dolu slotlara bakar → Y13.
- Kullanıcıya dönen metinler akış adına göre üretilir ("Ödeme" sabiti kalkar) → O15.
- `CLAUDE.md`, `README.md`, `ANALIZ.md`, `CHANGELOG.md` gerçeğe göre güncellenir —
  özellikle `CLAUDE.md` madde 1 (ölü sınıfı anlatıyor) ve madde 3 (çağrılmayan doğrulama).

---

## 4. Kabul Kriterleri

| Kriter | Nasıl doğrulanır |
|---|---|
| Başarılı transfer "tamamlandı" olarak raporlanır | Elle senaryo + `WorkflowTest` |
| Yetersiz bakiyede kullanıcı gerçek sebebi görür | Elle senaryo |
| "onaylamıyorum" para hareketi tetiklemez | Birim düzeyi senaryo |
| İki eşzamanlı oturum birbirinin verisini görmez | İki tarayıcı, paralel akış |
| Onayla'ya çift tıklama tek tahsilat üretir | Elle senaryo |
| Ekran yönlendirme (OPEN_SCREEN) çalışır | `test.http` / elle |
| YAML ile eklenen akış gerçek MCP aracı çağırır | `limit-artirim` uçtan uca |
| Ajan nesneleri singleton, kullanıcı verisi taşımıyor | Kod incelemesi + bellek profili |
| **Bakiye yetersizliği onay ekranından ÖNCE bildirilir** | Elle senaryo (Faz 2) |
| Geçersiz IBAN girilir girilmez reddedilir | Elle senaryo (Faz 2) |
| **Ajan katmanında hiçbir bankacılık kuralı/karşılaştırması yok** | Kod incelemesi — validator'lar tek satırlık delegasyon olmalı |
| Hiçbir MCP aracı veri katmanına doğrudan erişmiyor | Kod incelemesi / bağımlılık taraması |
| İstekler arasında bellekte akış durumu kalmıyor | Kod incelemesi + bellek profili |
| Hiçbir sınıf 250 satırı geçmiyor | Satır sayımı |

---

## 5. Riskler

| Risk | Etki | Azaltma |
|---|---|---|
| Otomatik test yok — regresyon riski yüksek | Her fazda | Faz 0'da `WorkflowTest` onarılır ve her fazda genişletilir |
| `AsyncLocal` scope'unun MAF'ın iç thread'lerine akmaması | Oturum izolasyonu bozulur | Faz 3 başında hedefli doğrulama; akmazsa `IWorkflowContext` üzerinden explicit taşıma |
| Erken doğrulama LLM'i kısır döngüye sokabilir (aynı slotu tekrar tekrar sorma) | Kötü UX | Bankanın `Hints` alanı kabul edilebilir aralığı döner (örn. `maxAllowedAmount: 12500`), mesaja eklenir; ardışık N reddedişte akış iptal önerisi |
| Ön-doğrulama ile işlem anı kuralları arasında kopya oluşması | Tutarsız davranış | İkisi de **banka tarafında**, aynı kural kod yolunu paylaşır. Ajan katmanı hiçbir kural içermediği için kopya oluşamaz |
| Gerçek backend'in dry-run çağrısı mock'tan farklı imzada çıkabilir | Faz 2 yeniden çalışma | Değişim `ICollectionService`/`ITransferService` implementasyonuyla sınırlı; `ISlotValidator` adapter'ı ve üstü etkilenmez |
| Faz 5 (parçalama) davranış kaydırabilir | Regresyon | Faz 1–4 sonrası, davranış korumalı, adım adım |
| `ResponseSanitizer` güvenlik sınırı | Sızıntı | Bu refactor'da **davranışı değişmiyor**; yalnızca fazla ctor siliniyor |

---

## 6. Kapsam Dışı (bilinçli)

- `/api/workflows/register-declarative` kimlik doğrulaması — iç ağ, sonra ele alınacak.
  (Path traversal düzeltmesi Y10 kapsamında yine de yapılıyor.)
- Redis / dağıtık store — `ISessionStateStore` soyutlaması bu planda konuluyor, implementasyon
  RAM'de kalıyor. Prod öncesi tek DI satırıyla değişecek. Amaç kalıcı depolama değil, kopan
  oturumların ~1 dakikalık kurtarma penceresi.
- MAF workflow checkpoint'i — değerlendirildi, gereksiz bulundu (bkz. 2.2).
- `InMemoryBankStore`'un gerçek bankacılık sistemiyle değiştirilmesi. Demo aşamasında her şey
  local çalışır. `ICollectionService.ValidatePayment` / `ITransferService.ValidateTransfer`
  gerçek backend'in dry-run çağrısının mock'udur; prod'da bu iki metodun gövdesi HTTP çağrısına
  dönüşür, ajan katmanı hiç değişmez.
- **Bankacılık iş kurallarını bizim yazmamız.** Bakiye/limit/IBAN kuralları bankanın
  sorumluluğundadır; sistemimiz yalnızca çağırır ve cevabı yüzeye çıkarır.
- Otomatik test altyapısı kurulumu (xUnit) — `WorkflowTest` onarımı ile yetiniliyor.
