# FİNAL PLAN — Sistemin Son Hâli

> Bu belge, `docs/PERFORMANS-VE-OLCEKLEME-PLANI.md` ve `docs/UYGULAMA-TALIMATI.md` ile daha önce
> konuşulan ajan ömrü / katalog / dokümantasyon isteklerini **tek plana** birleştirir ve hepsinin
> yerine geçer. İki eski belge **yürürlükten kalkar**: uygulama bitince silinir ya da
> `docs/arsiv/` altına taşınır. İki kaynak bırakmak, biri güncellenirken diğerinin sessizce
> bayatlaması demektir.

## Context

Sistem, var olan bir bankaya agentic katman ekler. Önceki turda tak-çalıştır engelleri kesildi
(`Banking.Domain` ve `Banking.Host` artık `Banking.BankApi`'ye referans vermiyor), karşılığı olmayan
soyutlamalar kaldırıldı ve bayat onay bağlamı hatası düzeltildi. Kalan iş üç başlıkta toplanıyor:

1. **Ölçeklenebilirlik** — bugün oturum tek sürecin belleğinde, tekrar koruması süreç yerel bir
   kilide bağlı, bir soru dört model turu harcıyor ve konuşma geçmişi hiç kırpılmıyor.
2. **Kaynak yönetimi** — ajan önbellekleri yalnızca büyüyor; ekran kodu kataloğu hâlâ süreç içi
   dosyadan geliyor ve semantik indeks her açılışta sıfırdan hesaplanıyor.
3. **Okunabilirlik** — gerekçe metot açıklamalarına birikmiş; metodun ne yaptığı görünmüyor.

**Hedef:** sistemin banka sunucusunda binlerce mobil kullanıcıya hizmet edebilecek son hâline gelmesi.

**Ölçüt** (eski plandan korunur): kullanıcı başına gecikme değil, **model turu başına maliyet**.

```
gereken tur/sn = (eşzamanlı aktif kullanıcı ÷ mesajlar arası süre) × tur/mesaj
```

2000 aktif kullanıcı ve 45 sn'de bir mesaj → 44 mesaj/sn. Bugünkü **4 tur/mesaj** ile 178 model
turu/sn gerekir; 2 tur/mesaj ile 88. Donanım faturasını belirleyen çarpan `tur/mesaj`'dır.

---

## Eski planların bugünkü karşılığı (koddan doğrulandı)

Önceki turdaki refactor iki belgedeki bazı maddeleri **kısmen yaptı ya da geçersiz kıldı.**
Uygulamaya başlamadan önce bu tablo okunmalıdır:

| Eski madde | Bugünkü durum | Kaynak |
|---|---|---|
| İŞ 1 — token ölçümü | ✅ **Hâlâ geçerli.** `TracingChatClient` içinde `Usage` veya `Stopwatch` yok | doğrulandı |
| İŞ 2 adım 4 — `SlotValues` set edilemez | ✅ **Hâlâ geçerli.** `SessionState.cs:30` hâlâ `{ get; }` | doğrulandı |
| İŞ 2 adım 5 — normalizasyon listesi | ⚠️ **Genişledi.** `SessionState`'e `LastAssistantReply` eklendi (düz `string`, normalizasyon gerektirmez ama gidiş-dönüşte korunmalı) | doğrulandı |
| İŞ 3 — kilit tablosu soyutlaması | ✅ **Hâlâ geçerli.** `_locksBySessionId` / `_lockTableGate` yerinde | doğrulandı |
| İŞ 4 — "yapılmayacak" notundaki `TopicContextProvider` atfı | ❌ **Ölü atıf.** O dosya silindi | doğrulandı |
| İŞ 6 adım 2 — `ChatClientFactory.cs`'e parametre ekle | ❌ **Geçersiz.** Dosya silindi, `TracedChatClientFactory`'ye katıldı | doğrulandı |
| İŞ 6 adım 3 — fabrika `ChatClient` yerine `AgentOptions` alsın | ✅ **Zaten yapıldı** (`TracedChatClientFactory.cs:20`) | doğrulandı |
| İŞ 6 adım 4 — DI'dan `ChatClient` kaydını kaldır | ✅ **Zaten yapıldı.** DI'da `ChatClient` kaydı yok | doğrulandı |
| Perf 1.3 — "tek `ChatClient` yapıcıya gelir" | ⚠️ **Mekanizma değişti**, sonuç aynı: hâlâ tek model | doğrulandı |
| Perf Faz 2 — `AIContextProvider` örneği `TopicContextProvider` | ❌ **Ölü atıf.** Yerine `AgentRuntime.AskLanguageModelAsync` bağlam enjeksiyonu gösterilmeli | doğrulandı |
| Perf 1.6 — banka `Idempotency-Key` okumuyor | ✅ **Hâlâ geçerli.** `Banking.BankApi` içinde hiç geçmiyor | doğrulandı |

**Sonuç: İŞ 6 üçte iki oranında bitmiş.** Kalan iş yalnızca `AgentOptions.Models` haritası ve model
adına göre `ChatClient` önbelleği; bu yüzden aşağıda erken bir faza alındı.

---

## Çalışma kuralları (her fazda geçerli)

1. **Bir seferde tek faz.** Faz bitip doğrulanmadan sonrakine geçilmez.
2. **Her fazın sonunda:** `dotnet build BankingAgents.sln` — **uyarı da hata sayılır.**
3. **Yeni `///` yazdıysan:**
   `dotnet build BankingAgents.sln -p:GenerateDocumentationFile=true -p:BaseOutputPath="$env:TEMP\docbuild\"`
4. **Dil:** kod yorumları, `///` blokları ve trace mesajları Türkçe. `docs/DOKUMANTASYON-STANDARDI.md`
   kurallarına uyulur (FAZ 9'da güncellenecek yeni kurallar dâhil).
5. **MAF/MCP API'sini ezberden yazma.** Bir tip veya üye derlenmezse tahminle düzeltme yapma:
   `dotnet run --project tools/ApiDump` ile gerçek yüzeyi dök, `docs/MAF-API-NOTES.md`'ye bak.
6. **Commit atma.** Değişiklikler working tree'de bırakılır.
7. **Tak-çalıştır kabul testi her fazdan sonra geçmeli:**
   ```powershell
   dotnet sln BankingAgents.sln remove src/Banking.BankApi/Banking.BankApi.csproj
   dotnet build src/Banking.Host        # DERLENMELİ
   dotnet sln BankingAgents.sln add src/Banking.BankApi/Banking.BankApi.csproj
   ```

**Test kurulumu** (üç terminal):

```powershell
dotnet run --project src/Banking.BankApi
dotnet run --project src/Banking.Host
dotnet run --project tools/LoadTest -- --users 20 --concurrency 8 --timeout 300
dotnet run --project tools/WorkflowTest    # modelsiz, deterministik
```

---

## FAZ 0 — Model turunun maliyetini ölç

**Amaç:** sonraki her fazın etkisi kanıtlanabilsin. Etkisi ölçülemeyen iyileştirme yapılmaz.

**Dosya:** `src/Banking.Agents/Tracing/TracingChatClient.cs` (tek dosya)

1. `GetResponseAsync` içinde model çağrısını `Stopwatch` ile ölç.
2. Yanıttaki `response.Usage` okunup `TraceHub` ile yayınlanır. **Ayrı log kanalı açma** —
   `TraceHub.Publish` kaydı hem canlı ize hem `ILogger`'a düşürüyor.
3. Kategori `TraceCategories.Llm`; metin `"{label} -> Token Maliyeti"`, detay
   `"girdi=<n> çıktı=<n> toplam=<n> süre=<ms> ms"`.
4. `Usage` `null` ise token satırı yayınlanmaz (bazı sağlayıcılar döndürmez); süre yine yayınlanır.
5. `GetStreamingResponseAsync` kapsam dışı — sistem akış kullanmıyor.

**Tuzak:** tip `Microsoft.Extensions.AI.UsageDetails`, alanları `long?`. Derlenmezse kural 5.
Sınıf *primary constructor* kullanıyor: `hub` ve `label`'a metot içinden doğrudan erişilir.
Ortak hazırlık `PrepareTurn` içinde; ölçüm oraya değil, `GetResponseAsync`'e eklenir.

**Kabul:** `/api/chat`'e tek soru → konsolda o isteğin **her** model turu için etiket + girdi/çıktı
token + süre satırı. Basit soruda beklenen satır sayısı **4**.

---

## FAZ 1 — Ajanlar ihtiyaç anında kurulsun, boşta kalan bırakılsın

**Amaç:** açılıştaki toplu ajan kurulumu kalksın; ajan ilk istendiğinde kurulsun, boşta kalan tahliye
edilsin. **Asıl kazanç bellek tasarrufu değil, sınırsız büyümenin durmasıdır:**
`SpecialistCatalog._agents` ve `WorkflowAgentFactory._agentsByName` yalnızca büyüyor ve çalışma
zamanında YAML ile ajan eklemek sistemin tasarım hedefi.

**Karar:** derlenmiş uzman kartları (`HesapUzmaniAgent`, `KartUzmaniAgent`, `AnalizUzmaniAgent`)
**C#'ta kalır.** YAML'a taşınmaz — `StartupLoader` bozuk YAML'ı yakalayıp devam ettiği için bir dosya
bozulduğunda uzman sessizce yok olurdu. Değişen tek şey ajan **nesnesinin** ömrü.

### 1.0 Önce bu tuzağı çöz — yoksa tahliye hiçbir şey boşaltmaz

`SpecialistCatalog.BuildBridgeTools()` süreç içi modda (`UseA2AForSpecialists: false`) somut ajanı
alıp `AsAIFunction` ile **köprü aracının içine yakalar** (`Specialists/SpecialistCatalog.cs:230-232`).
Köprü araçları orkestratörün araç listesinde, orkestratör ise uzun ömürlü singleton. Dolayısıyla
sözlükten tahliye **hiçbir şey serbest bırakmaz.** A2A modunda köprü bir `A2AClient` tutar ve tahliye
çalışır — iki mod arasında davranış farkı kabul edilemez.

**Yapılacak:** köprü aracı ajanı yakalamasın, çağrı anında adına göre çözsün.

> Deseni sıfırdan yazma: `Specialists/SpecialistRoutingAgent.cs` tam olarak bunu yapan bir vekil
> (`AIAgent`'tan türer, hedefini her çağrıda çözer). `LazySpecialistAgent(catalog, name)` aynı şeklin
> daha basiti olur; `RunCoreAsync`, `RunCoreStreamingAsync`, `CreateSessionCoreAsync`,
> `SerializeSessionCoreAsync`, `DeserializeSessionCoreAsync` hedefe devreder.

`BuildBridgeTools` süreç içi dalında `GetSpecialist(card.Name)` yerine bu vekil verilir. Köprü
aracının şeması ve davranışı değişmez.

### 1.1 Önbellekleri süre sınırlı yap

**Yeni altyapı yazma:** `IMemoryCache` zaten DI'da kayıtlı ve kayan süre + tahliye geri çağrımı
destekliyor. Kullanılacak desen `Sessions/InMemorySessionStore.cs` içinde hazır.

| Dosya | Değişiklik |
|---|---|
| `Specialists/SpecialistCatalog.cs` | `_agents` → `IMemoryCache` (`specialist-agent:{ad}`) |
| `Workflows/Engine/WorkflowAgentFactory.cs` | `_agentsByName` → `IMemoryCache` (`workflow-agent:{ad}`) |
| `Runtime/AgentOptions.cs` | `AgentIdleTimeoutMinutes` (varsayılan **20**) + `AgentIdleTimeout` |

`AgentIdleTimeout` doğrulaması `SessionStoreOptions.SlidingTimeToLive` desenini izler: sıfır/negatif
değer istisna fırlatır, sessizce varsayılana düşmez. Tahliye geri çağrımı `TraceHub`'a bir satır
yazar — hangi ajanın ne zaman bırakıldığı izde görünmezse gecikme artışının nedeni okunamaz.

### 1.2 Toplu kurulumu kaldır

`SpecialistCatalog.EnsureInitializedAsync` bugün kayıtlı her uzmanı sırayla kurar. Bu döngü kalkar;
metodun kalan işi MCP bağlantısını hazır etmek ve `Transport` değerini yazmaktır. Kurulum
`GetSpecialistAsync(name)` içine taşınır:

- Kart yoksa → `ArgumentException` (bilinmeyen uzman) — **bugünkü davranış korunur.**
- Kart var, ajan önbellekte yok → `BuildFromCardAsync` ile kurulur ve önbelleğe yazılır.

**Bu iki durum karıştırılmamalı:** "böyle bir uzman yok" bir yapılandırma hatasıdır ve görünmelidir;
"ajan tahliye edilmiş" olağandır ve sessizce çözülmelidir.

`SpecialistRoutingAgent.ResolveTargetAsync` ve `GetSpecialistByA2ARoute` yeni yola bağlanır.
Eşzamanlı ıskalar ajan adı başına bir `SemaphoreSlim` ile tekilleştirilir —
`McpToolProvider._connectionGate` ve `AgentRuntime._buildGate` aynı deseni kullanıyor.

### 1.3 `Version` ile tahliyeyi ayır (regresyon riski en yüksek nokta)

`SpecialistCatalog.Version` **katalog içeriği değiştiğinde** artar ve orkestratör ile tüm akış
ajanlarının yeniden kurulmasını tetikler. **Tahliye `Version`'ı artırmamalıdır** — artırırsa boştaki
tek bir uzmanın düşmesi topluca yeniden kurulum fırtınası üretir.

### 1.4 Ön ısıtma

`ScheduleAgentWarmUp` → `EnsureReadyAsync` **kalır**: pahalı olan MCP loopback bağlantısı ve
orkestratör kurulumudur, ikisi de her istekte kullanılır. Ön ısıtma artık uzmanları tek tek kurmaz.

**Kabul:** `/api/diag` beş uzmanı listelemeye devam ediyor (liste kartlardan gelir);
`AgentIdleTimeoutMinutes: 1` ile tahliye satırı düşüyor ve sonraki istek yeniden kurup doğru yanıt
veriyor; **aynı davranış `UseA2AForSpecialists: false` ile de geçerli**; deklaratif uzman kaydı
sonrası orkestratör gereksiz yere yeniden kurulmuyor.

---

## FAZ 2 — Katalogları bankaya taşı, indeksi bir kez kur

### 2.1 Bugünkü durum

| Katalog | Kaynak | Durum |
|---|---|---|
| Kampanya | `POST /api/campaigns` | ✅ taşındı |
| Finansman | `POST /api/financing-options` | ✅ taşındı |
| **Ekran kodu (mkCode)** | `MkCodesRegistry` statik sınıf | ❌ süreç içi dosya |

`MkCodesRegistry` statik kurucusunda **altı aday dosya yolu** deniyor — `xmlviewer (1).xml`,
`mkcodes.json` ve `..\..\..\..` ile dizin dışına çıkanlar. Bu, temizlenen tak-çalıştır sızıntılarının
aynı ailesinden.

### 2.2 mkCode'ları bankaya taşı

| Dosya | İşlem |
|---|---|
| `src/Banking.BankApi/Program.cs` | `POST /api/mkcodes` ucu |
| `src/Banking.Mcp/Constants/BankApiPaths.cs` | `MkCodes` sabiti |
| `src/Banking.Mcp/Semantic/MkCodeCatalog.cs` | **yeni** — bankadan çeker, kod→tanım araması + indeks |
| `src/Banking.Domain/MkCodes/` | `MkCodesRegistry` silinir; arayıcı `Banking.Mcp/Semantic/`'e taşınır |
| `src/Banking.Gateway/GatewayResponseMapper.cs` | `MkCodesRegistry.Get` → enjekte edilen katalog |
| `src/Banking.Agents/Runtime/NavigationTools.cs` | arayıcının yeni konumu |

**Kod→tanım araması kaybolmamalı.** `MkCodesRegistry.Get`, `GatewayResponseMapper.BuildConfirmationScreen`
içinde onay ekranının kodunu çözerken kullanılıyor (`GatewayResponseMapper.cs:89`). Yeni katalog hem
`Get(value)` hem indeks beslemesi sunar.

**Varsayılan kodlar (`AddDefaultCodes`) kaldırılır.** Beş kodluk sessiz yedek, banka erişilemezken
"çalışıyor" görüntüsü verir. `GatewayResponseMapper` kod çözülemediğinde zaten uyarı loglayıp akış
adını taşıyor — görünür bozulma, sessiz yanlış ekrandan iyidir.

Kampanya/finansman ile aynı dayanıklılık kuralı: banka ayakta değilse indeks boş kalır,
`_index.Count == 0` kontrolü bir sonraki çağrıda yeniden dener, açılış durdurulmaz.

### 2.3 "Bir kez indeksle" — depo seam'i

Bugün her açılışta 266 mkCode + kampanya + finansman yeniden vektörleştiriliyor.

`Banking.Domain/Semantic/` altına dar bir sözleşme:

```csharp
public interface ISemanticIndexStore
{
    ValueTask<IReadOnlyList<StoredVector>?> TryLoadAsync(string indexKey, CancellationToken ct);
    ValueTask SaveAsync(string indexKey, IReadOnlyList<StoredVector> vectors, CancellationToken ct);
}
```

- **Bu fazda:** `InMemorySemanticIndexStore`. Tek örnekli çalışmada açılış maliyetini değiştirmez ama
  seam'i yerine koyar ve `SemanticIndex<T>` API'sini sabitler.
- **FAZ 5'te:** `RedisSemanticIndexStore` tek dosyayla eklenir; Redis paketi FAZ 4'te geliyor.

**`indexKey` bayatlamayı kendisi çözmeli:** çekilen katalog yükünün içerik özetini (hash) taşır. Banka
kataloğu değiştiğinde anahtar değişir ve indeks otomatik yeniden hesaplanır. Sürümsüz bir anahtar,
bankada güncellenmiş bir kampanyanın aramada hiç görünmemesine yol açar.

> **Bloke edici ön araştırma:** `SmartComponents.LocalEmbeddings.EmbeddingF32` serileştirilebilir mi?
> `dotnet run --project tools/ApiDump` ile gerçek yüzey dökülüp bakılır. Doğrudan serileştirilemiyorsa
> ham `float[]` saklanıp yüklemede yeniden kurulur. **Ezberden yazma.**

### 2.4 Bu, FAZ 5'teki araç önbelleğiyle aynı şey değil

| | Araç sonucu önbelleği (FAZ 5) | İndeks kalıcılığı (bu faz) |
|---|---|---|
| Neyi saklar | MCP **araç yanıtı** (JSON) | **Vektörler** |
| Nereye takılır | `McpToolProvider`'ın dağıttığı araçlara dekoratör | `SemanticIndex<T>` |
| Ne kazandırır | Banka HTTP çağrısı | Gömme hesabı |

Semantik arayıcılar MCP araçlarından geçmez — `SemanticCampaignSearcher.RefreshAsync` doğrudan
`bank.AskBankAsync` çağırır. Dekoratör bu yolu **kapsamaz**; ikisi birbirinin yerine geçmez.

**Kabul:** banka kapalıyken host açılıyor, arama boş dönüyor ve izde "indeks boş" var; banka açılınca
ilk aramada doluyor. `POST /api/mkcodes` izde görünüyor. Ödeme akışı onay ekranını doğru mkCode ile
açıyor.

---

## FAZ 3 — Ucuz tur azaltma

FAZ 0 ölçümü olmadan başlanmaz. Redis'e bağımlı değildir.

### 3.1 Ajan başına model (kalan iş küçüldü)

Orkestratörün 1. turu bir **sınıflandırma** işidir: hangi uzman, hangi akış. Küçük bir model bunu
yapar; büyük modelin turları yarıya iner.

Önceki turda `ChatClientFactory` `TracedChatClientFactory`'ye katıldığı ve DI kaydı kaldırıldığı için
eski talimatın 2. ve 4. adımları düştü. **Kalan iş:**

1. `AgentOptions`'a `Dictionary<string, string> Models` (OrdinalIgnoreCase). Anahtar
   `TracedChatClientFactory.Create(label)` etiketi (örn. `Orkestratör`), değer model adı. Karşılığı
   olmayan etiket `Model` alanındaki varsayılana düşer.
2. `TracedChatClientFactory`: bugün yapıcıda tek bir `ChatClient` kuruyor
   (`TracedChatClientFactory.cs:22`). Bunun yerine model adına göre `ConcurrentDictionary` içinde
   önbellek; `CreateChatClient` model adını parametre alır. **`Create(label)` imzası değişmez.**
3. `appsettings.json`'a boş bir `Agents:Models` bölümü — varsayılan davranış değişmesin.

**Kabul:** `Agents:Models` boşken davranış aynı; `Agents:Models:Orkestratör` küçük bir modele
ayarlandığında FAZ 0 ölçümünde orkestratör turlarının model adı değişiyor, uzman turları büyük
modelde kalıyor, akışlar çalışıyor.

### 3.2 Tek turda çoklu araç çağrısı

Model birden fazla aracı aynı turda çağırabilir; sırayla çağırdığında her araç bir tur maliyetine
dönüşür. Slot doldurmada karşılığı zaten var (`AgentPrompts.SlotTurnDirective`); aynı yönerge uzman
talimatlarına da yazılır.

**Kısıt:** yönerge alan adı/araç adı saymaz, genel ilke olarak yazılır
(`docs/PROMPT-YAZIM-REHBERI.md`).

---

## FAZ 4 — Redis: oturum deposu ve dağıtık kilit

**İki iş tek fazda yapılır.** Depoyu Redis'e taşıyıp kilidi süreç yerel bırakmak, çift tahsilat
korumasını sessizce zayıflatır: iki örnekte aynı oturumun iki eşzamanlı onayı farklı süreçlere düşer,
ikisi de `CompletedOutcome`'ı boş görür, ikisi de tahsil eder.

### 4.1 Paketler ve ayarlar

```powershell
dotnet add src/Banking.Agents package Microsoft.Extensions.Caching.StackExchangeRedis
dotnet add src/Banking.Agents package StackExchange.Redis
```

İkisi de gerekli: depo `IDistributedCache` kullanır (kayan TTL'i kendi yönetir), kilit ve önbellek
`IConnectionMultiplexer` üzerinden ham komut çalıştırır.

`SessionStoreOptions`'a: `string? RedisConnectionString`, `int LockTimeToLiveSeconds = 300`.
`SlidingTimeToLiveMinutes` ve `SlidingTimeToLive` üyelerine dokunulmaz.

### 4.2 `RedisSessionStore`

`ISessionStore`'u uygular; `IDistributedCache` + `IOptions<SessionStoreOptions>` alır.
Anahtar `banking-session:{sessionId}` (bellek içi uygulamayla aynı ön ek).
Serileştirici sınıf içinde `static readonly` ve Türkçe kaçışını kapatır
(`Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)`).

**Bağlantı hatasında istisnayı yutma.** `AmbientSessionAccessor.OpenAsync` okuma hatasını zaten ele
alıyor (kilidi bırakıp fırlatıyor) ve kullanıcı görünür hata alır. Sessiz bir `null`, oturumu yeni
gibi göstererek yarım kalan akışı siler.

### 4.3 İki serileştirme tuzağı — atlanırsa sessizce bozulur

**a) `SlotValues` doldurulmaz.** Alan `public Dictionary<string, object?> SlotValues { get; }`
(`SessionState.cs:30`) — *set edilemez*. `System.Text.Json` böyle bir koleksiyonu varsayılan
davranışta **doldurmaz**: gidiş-dönüşten sonra sözlük boş döner ve akış toplanmış tüm slotları
kaybeder. Çözüm:

```csharp
[System.Text.Json.Serialization.JsonObjectCreationHandling(
    System.Text.Json.Serialization.JsonObjectCreationHandling.Populate)]
```

`StringComparer.OrdinalIgnoreCase` karşılaştırıcısının gidiş-dönüşten sonra korunduğu doğrulanır;
korunmuyorsa alan `{ get; set; }` yapılıp `TryGetAsync` içinde sözlük yeniden kurulur.

**b) İdempotency parmak izi kayar.** `SlotValues`, `ProposedSlots`, `PendingConfirmation.Parameters`
ve `CompletedOutcome.Details` sözlükleri `object?` taşır; JSON gidiş-dönüşünden sonra değerler
`JsonElement`'e döner. Parmak izi (`GenericConfirmationView.ComputeIdempotencyToken`) değerlerin
**metin hâlinden** hesaplandığı için gidiş-dönüşten önce ve sonra **aynı** çıkmalıdır; farklı çıkarsa
aynı teklif için ikinci onay yeni bir para hareketi başlatır.

Normalizasyon yardımcısı: `JsonElement` → CLR ilkel tipi (`String`→`string`, `Number`→`decimal`,
`True`/`False`→`bool`, `Null`→`null`); dizi ve nesne metin hâliyle bırakılır. Uygulanacak yerler:
`SlotValues`, `ProposedSlots`, `PendingConfirmation.Parameters`, `CompletedOutcome.Details`.
**Yeni alan:** `LastAssistantReply` düz `string`; normalizasyon gerektirmez ama gidiş-dönüşte
korunduğu doğrulanır.

### 4.4 Dağıtık kilit

| Dosya | İşlem |
|---|---|
| `Sessions/ISessionLockProvider.cs` | **yeni** |
| `Sessions/InProcessSessionLockProvider.cs` | **yeni** (mevcut kod taşınır) |
| `Sessions/RedisSessionLockProvider.cs` | **yeni** |
| `Sessions/AmbientSessionAccessor.cs` | kilit tablosu çıkarılır |
| `Sessions/SessionLease.cs` | `Lock` alanının tipi `IAsyncDisposable?` olur |

```csharp
public interface ISessionLockProvider
{
    Task<IAsyncDisposable> AcquireAsync(string sessionId, CancellationToken cancellationToken);
}
```

`InProcessSessionLockProvider`: `_locksBySessionId`, `_lockTableGate`, `AcquireLockHandle`,
`ReleaseLockHandle` ve `SessionLock` tipi **olduğu gibi** taşınır. Bekleyen sayacı sıfıra düşünce
kilidin sözlükten silinmesi, sözlüğün sınırsız büyümesini engelleyen kuraldır — davranış değiştirilmez.

`RedisSessionLockProvider`: anahtar `banking-session-lock:{sessionId}`, değer her alımda üretilen
benzersiz jeton. Alma `StringSetAsync(..., When.NotExists)`, `false` dönerse 25-50 ms gecikmeyle
yeniden dene; iptal edilirse `OperationCanceledException`. TTL `LockTimeToLiveSeconds` — **en uzun
istek süresinden büyük olmalı.** Bırakma jetonu karşılaştıran Lua betiğiyle:

```
if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end
```

**Koşulsuz `KeyDeleteAsync` kullanma:** TTL dolup kilidi başka bir istek almışsa onun kilidini silersin.

**İç içe scope yolunu bozma.** `OpenAsync`, aynı oturum zaten aktifse kilide *hiç dokunmadan*
`SessionLease.Nested(...)` döner. Bu sıra korunmalı; kilit alınmaya çalışılırsa istek kendi kilidini
bekleyip kilitlenir. Durum yükleme başarısız olduğunda kilidi bırakan `catch` bloğu da korunur.

### 4.5 Koşullu kayıt

`ServiceRegistration`: `Sessions:RedisConnectionString` **boşsa** `InMemorySessionStore` +
`InProcessSessionLockProvider`; **doluysa** `RedisSessionStore` + `RedisSessionLockProvider` +
`AddStackExchangeRedisCache` + `IConnectionMultiplexer`. `AddMemoryCache()` kalır — FAZ 1 ajan
önbelleği onu kullanıyor.

**Redis yapılandırılmadığında sistem bugünkü gibi çalışmalı;** geliştirme ortamı Redis kurulumuna
bağımlı hâle getirilmez.

### 4.6 Doğrulama senaryosu

`tools/WorkflowTest`'e model ve Redis gerektirmeyen saf serileştirme testi eklenir: dolu bir
`SessionState` kur → parmak izini hesapla → aynı serileştiriciyle JSON'a çevir, geri çöz, normalize et
→ `SlotValues` sayısı/değerleri aynı, sözlük harf duyarsız, **parmak izi birebir aynı**,
`PendingConfirmation.Parameters` aynı.

**Bilinen sınırlama (kabul edilir):** bellek içi depo süresi dolan oturumu ize yazıyor
(`InMemorySessionStore.PublishEviction`). Redis'te bu geri çağrım yoktur. Keyspace notification
kurmaya çalışma; sınırlamayı `RedisSessionStore` `///` bloğunda belirt.

**Kabul:** `tools/WorkflowTest` geçiyor; Redis boşken davranış aynı; Redis doluyken ödeme akışını onay
ekranına getir → **host'u yeniden başlat** → aynı `sessionToken` ile "onaylıyorum" → işlem
tamamlanıyor. `tools/LoadTest -- --users 40 --concurrency 40 --mix double=100` çift onay
senaryolarının tamamında **tek makbuz**, `DOUBLE_CHARGE` ihlali yok — hem Redis açıkken hem kapalıyken.
`--mix pay=100` normal tamamlanıyor (iç içe scope yolu bozulmamış).

---

## FAZ 5 — Redis'e dayanan önbellekler

### 5.1 Semantik indeks kalıcılığı

FAZ 2.3'teki seam'in Redis uygulaması: `RedisSemanticIndexStore`. `IConnectionMultiplexer` kullanır,
anahtar FAZ 2.3'teki içerik özetli `indexKey`. Çok örnekli dağıtımda her örneğin aynı katalogları
yeniden vektörleştirmesini engeller.

### 5.2 Araç sonucu önbelleği

Aynı bilgi bir akış içinde tekrar tekrar çekiliyor. Sonucu önbelleğe almak banka çağrısını siler.

| Dosya | İşlem |
|---|---|
| `Mcp/IToolResultCache.cs` | **yeni** (arayüz + `DisabledToolResultCache`) |
| `Mcp/RedisToolResultCache.cs` | **yeni** |
| `Mcp/ToolCachePolicy.cs` | **yeni** (araç → TTL) |
| `Mcp/CachingAIFunction.cs` | **yeni** (dekoratör) |
| `Mcp/McpToolProvider.cs` | dekoratörü devreye al |
| `Workflows/Actions/RestWorkflowExecutor.cs` | geçersiz kılma tetiği |

Arayüz: `TryGetAsync`, `SetAsync`, `GetCustomerVersionAsync`, `InvalidateCustomerAsync`.
Aynı dosyada hiçbir şey yapmayan `DisabledToolResultCache` bulunur — Redis yoksa o kaydedilir,
böylece dekoratör her zaman devrede kalır ve **iki ayrı kod yolu oluşmaz.**

**Katmanlar:**

| Katman | İçerik | TTL | Anahtarda müşteri |
|---|---|---|---|
| T1 | Kampanya/finansman araçları (`GetCampaings`, `GetFinancingOptions`) | ~15 dk | yok |
| T2 | Hesap listesi, kart listesi, kapanmış dönem ekstre analizi | 60 sn | var |
| T3 | Bakiye, güncel borç, kullanılabilir limit | 10 sn | var |
| T4 | Para hareketi uçları **ve ön doğrulama** | önbelleklenmez | — |

T4'te ön doğrulamanın bulunması bilinçlidir: bayat bir doğrulama, gerçekleşmeyecek bir işlemi
onaylanabilir gösterir. Araç adları `*ToolNames` sabitlerinden okunur, **metin olarak yazılmaz.**
Politikada karşılığı olmayan araç **önbelleklenmez** (güvenli taraf).

`CachingAIFunction`, `TracingAIFunction` ile birebir aynı desende bir dekoratör. Anahtar
`arac:{sürüm}:{müşteri}:{araçAdı}:{argümanÖzeti}`; `argümanÖzeti` argümanların **anahtara göre
sıralanmış** kanonik JSON'unun SHA-256 özetidir — sıralama olmazsa aynı çağrı farklı anahtarlara
düşer. Sarmalama sırası `CachingAIFunction(TracingAIFunction(mcpTool))`: izleme içte, önbellek dışta.

**Sonucun tipi korunmalı.** Araçlar `Task<object>` döndürüyor; önbellekten dönen değer araçtan dönenle
**aynı biçimde** serileşmeli. `JsonNode`/`JsonElement` uygundur; ham `string` döndürmek yanıtı çift
kaçışlı metne çevirir. İstisnalar önbelleğe **yazılmaz.**

**Geçersiz kılma:** `RestWorkflowExecutor.ExecuteAsync` içinde banka çağrısı **başarıyla** döndükten
sonra `InvalidateCustomerAsync(...)`. Müşteri kimliği `CustomerContext`'ten. Sürüm sayacının bir
artması o müşterinin tüm T2/T3 kayıtlarını tek işlemde geçersizleştirir; anahtar taraması gerekmez.
`RestWorkflowExecutor`'ın diğer davranışlarına dokunulmaz.

**Ölçüm:** FAZ 5 öncesinde bir `tools/LoadTest` koşumu yapılıp `Banking.BankApi` konsolundaki istek
sayısı kaydedilir; sonrasında karşılaştırılır.

**Kabul:** aynı soru ikinci kez sorulduğunda bankaya giden istek azalıyor; ödeme sonrası sorulan
bakiye **güncel** dönüyor (geçersiz kılma çalışıyor); para değişmezleri geçiyor.

---

## FAZ 6 — Geçmiş kırpma ve bağlam enjeksiyonu

### 6.1 Konuşma geçmişini kırp — **önce keşif**

Bugün geçmiş hiç kırpılmıyor; araç sonuçlarının tamamı her turda yeniden gönderiliyor
(`AgentConversationStore` konuşmayı olduğu gibi serileştiriyor).

**Doğrudan koda başlanmaz:**

1. `dotnet run --project tools/ApiDump` → `AgentSession` / `ChatClientAgent` yüzeyinde konuşma
   mesajlarına erişim veren üye var mı?
2. Bulgu `docs/MAF-API-NOTES.md`'ye kısa not olarak eklenir.
3. **Erişim varsa:** kırpma `AgentConversationStore.CaptureAsync` çağrılmadan önce yapılır. Kural: son
   N kullanıcı turu tam korunur; daha eski turlarda araç sonucu içerikleri düşürülür, kullanıcı ve
   asistan metinleri kalır. N yapılandırmadan (`Sessions:RetainedTurnCount`, varsayılan 6).
4. **Erişim yoksa:** iş burada bırakılır ve bulgu bildirilir. Serileştirilmiş MAF JSON'unu elle kesip
   biçmeye **kalkışma** — iç şema sürümle değişir ve bozulan geçmiş sessizce yanlış cevaba dönüşür.

Slot durumu geçmişte değil oturumda tutulduğu için (`SessionContext.GetSlotStatus()`) kırpma akışı
bozmaz.

### 6.2 Araç sonucunu bağlam olarak enjekte et

Önbellek banka çağrısını siler; sonucu modele **bağlam olarak vermek** bir model turunu siler. Asıl
kazanç ikincisidir. FAZ 5.2 doğrulanmadan başlanmaz.

Oturumun son N araç sonucu, her biri **zaman damgasıyla** bağlama enjekte edilir.

> **Desen için doğru örnek:** `AgentRuntime.AskLanguageModelAsync` içindeki aday akış ve önceki tur
> bağlamı enjeksiyonu. (Eski planlar burada `ContextProviders/TopicContextProvider.cs`'i gösteriyordu;
> **o dosya silindi** — atıf ölüdür.)

Risk gerçek: bağlama konan bayat bir bakiye güncelmiş gibi sunulabilir. İki kural sınırlar. Birincisi,
enjekte edilen her değer **yaşını taşır** ve talimat, para hareketi içeren kararlarda değerin yeniden
okunmasını şart koşar. İkincisi, asıl güvence mimaride: banka hem ön doğrulamayı hem tahsilatı aynı
kilit altında yeniden çalıştırır — bayat bir değerle hatalı para hareketi yapılamaz, en kötü hâlde
kullanıcı bayat bilgi okur.

### Hedef tablo

| Yol | Bugün | FAZ 3 sonrası | + FAZ 6.2 |
|---|---|---|---|
| Basit soru (büyük model turu) | 4 | 2 | 1 |
| Basit soru (küçük model turu) | 0 | 1 | 1 |
| Ekran açma (büyük model turu) | 3 | 1 | 1 |

**Kabul:** uzun sohbette (8+ tur) son turun girdi token sayısı FAZ 0 ölçümüne göre düşmüş; ödeme akışı
hâlâ onaya ulaşıyor; yanıt kalitesi `docs/PROMPT-YAZIM-REHBERI.md` manuel vakalarıyla doğrulanmış.

### Uygulanmayacak: "çağır ve özetle" yerine "devret"

Bugün uzmanın cevabı orkestratöre araç sonucu olarak döner ve orkestratör aynı cevabı baştan yazar
(4. tur). Cevabı doğrudan taşımak bir turu siler; karşılığında orkestratör iki uzmanın cevabını
birleştiren yanıtlar kuramaz olur. **Bu bir mimari karardır ve karar verilmeden uygulanmaz.**

---

## FAZ 7 — Çıkarım katmanı (kod değişikliği yok)

- **Continuous batching yapan bir sunucuya geç** (vLLM, SGLang, TensorRT-LLM). Fark ayar değil mimari:
  Ollama isteği bir yuvaya alıp bitene kadar tutar, diğerleri her adımda batch'i yeniden kurar.
  Uygulama tarafında değişiklik yok — `TracedChatClientFactory` yalnızca OpenAI uyumlu bir endpoint
  görür; `Agents:Endpoint` ve `Agents:Model` yeter.
- **Prefix caching'i kullan.** Orkestratörün talimatı ve araç şemaları her kullanıcıda birebir aynı;
  ortak önek bir kez hesaplanır. Kod buna hazır: talimat istek başına değişmez, aday akışlar ve önceki
  tur bağlamı geçmişin sonuna ayrı mesaj olarak eklenir ve öneki bozmaz. **Bu bir kısıttır:** talimata
  istek başına değişen bir değer (tarih, oturum bilgisi) eklemek tüm kullanıcıların önbelleğini
  geçersizleştirir.
- **Muhakeme seviyesini işe göre ayarla.** Muhakeme token'ları üretilen token'lardır; yönlendirme ve
  slot doldurmada düşük, finansal analizde yüksek olmalı. *Doğrulanması gereken:* değerin kurulu
  `Microsoft.Extensions.AI` + OpenAI SDK üzerinden nasıl geçirildiği sürüme bağlıdır; reflection'la
  kontrol edilmeden yazılmaz.
- **Ollama'da kalınacaksa:** `OLLAMA_NUM_PARALLEL`, `OLLAMA_MAX_QUEUE`, `OLLAMA_KEEP_ALIVE=-1`,
  `OLLAMA_FLASH_ATTENTION=1`, `OLLAMA_KV_CACHE_TYPE=q8_0`, `OLLAMA_CONTEXT_LENGTH`. Denge:
  **KV bellek = paralel yuva × bağlam uzunluğu.** 128k bağlamla çok yuva açılamaz; gerçek ihtiyaç
  kadar bağlam (8-16k) + çok yuva doğru tercihtir.

**Kabul:** toplu istek altında ölçülmüş tur/sn, Context'teki formülle hedeflenen eşzamanlı kullanıcı
sayısını karşılıyor.

---

## FAZ 8 — Çok örnekli dağıtım

- FAZ 4 tamamlanmış olmalı (Redis deposu **ve** dağıtık kilit).
- **Bankanın `Idempotency-Key` başlığını onurlandırdığı doğrulanmalı.** Koddan doğrulandı: vekil banka
  bu başlığı **hiç okumuyor**. Gerçek banka ucu aynı anahtarla gelen ikinci isteği yeni bir para
  hareketine çevirmiyorsa, tekrar koruması uygulama katmanına bağımlı kalır — ve o katman artık tek
  süreç değildir.
- Süreç içi MCP sunucusu ve A2A uçları örnek başına ayağa kalkar; `Agents:SelfBaseUrl` **her örneğin
  kendi adresini** göstermeli (bugün yapılandırmadan sabit geliyor).
- `tools/LoadTest` iki örneğe karşı yük dengeleyici üzerinden koşulur; çift onay senaryosu bu kurulumda
  ayrıca sınanır.

---

## FAZ 9 — Dokümantasyon üslubu

**En sonda yapılır:** kod oturmadan yazılan doküman iki kez yazılır.

### 9.1 Önce standardı güncelle

`docs/DOKUMANTASYON-STANDARDI.md` bugün S1–S10'u taşıyor ve S3 "gerekçe, kuralın kendisi olarak
yazılır" diyor — ama gerekçenin **nereye** yazılacağını söylemiyor. Gerekçe bu yüzden metotlara
birikti. Üç kural eklenir:

- **S11 — Metot açıklaması kısadır.** Bir, en çok iki düz cümle: ne yapar ve çağıranın bilmesi gereken
  tek şey. Metot açıklamasında `<para>` kullanılmaz.
  > Hedef üslup: *"Kullanıcı mesajını işler. Mesaja semantik olarak yakın akışlar orkestratöre bağlam
  > olarak verilir; kararı orkestratör verir."*
- **S12 — Gerekçe tipe aittir.** Tasarım kararı, takas, başarısızlık kipi ve "neden böyle değil"
  tipin (`class`/`record`/`interface`) açıklamasında durur. Tip açıklaması uzun ve ayrıntılı olabilir.
- **S13 — Ardışık akışlarda seyrek adım yorumu.** Metot birbirini izleyen adımlardan oluşuyorsa, adımı
  gerçekleştiren satırın üstüne 2–3 kelimelik bir `//` yorum konur. **Her satıra değil.**

### 9.2 Kod taraması — klasör klasör

Sıra: `Workflows/` → `Sessions/` → `Runtime/` → `Specialists/` → `Gateway/` → `Mcp/` → `Domain/`.

**Kural: gerekçe silinmez, taşınır.** Aşağıdaki metotlar güvenlik veya doğruluk sözleşmesi taşıyor;
içerikleri kaybolursa sistemin neden öyle yazıldığı kaybolur:

| Metot | Kaybolmaması gereken |
|---|---|
| `RestWorkflowExecutor.ExecuteAsync` | "Hiçbir koşulda istisna sızdırmaz" (MAF event akışı) |
| `WorkflowEngine.ApplyApprovalDecisionAsync` | Anlaşılmayan yanıt onay sayılmaz; idempotency |
| `WorkflowEngine.ConcludeSlotFillingAsync` | Onay kapısı `CallsBank`'e bağlıdır |
| `WorkflowEngine.InterpretAgentOutputAsync` | Kontrol sırasının bağlayıcılığı |
| `SessionContext.ResetWorkflow` | Orkestratör geçmişinin de temizlenme gerekçesi |
| `SessionLease.Enter` | `Enter()` neden asla `async` olamaz |
| `AmbientSessionAccessor.OpenAsync` | İç içe farklı oturum yasağı |
| `SchemaValidatedSlotWriter.WriteSlotIfSchemaAccepts` | Değer dönüştürülmez, reddedilir |
| `SemanticIndex.Search` | Eleme sıralamadan ve kotadan önce |
| `McpToolProvider.GetTracedToolsByNamesAsync` | Bilinmeyen araç adı sessizce atlanmaz |
| `SpecialistCatalog.GuardAgainstCompiledCollision` | Korumanın ad ile sınırlı olmadığı |
| `GenericConfirmationView.ComputeIdempotencyToken` | Tek yerde hesaplanma zorunluluğu |
| `RedisSessionLockProvider` (yeni) | Koşulsuz silme neden kullanılmaz |
| `RedisSessionStore` (yeni) | Tahliye izinin Redis'te bulunmaması |

### 9.3 Belge tekilleştirmesi

- `docs/PERFORMANS-VE-OLCEKLEME-PLANI.md` ve `docs/UYGULAMA-TALIMATI.md` **yürürlükten kalkar** —
  silinir ya da `docs/arsiv/` altına taşınır. İçerikleri bu plana taşındı.
- `CLAUDE.md`, `ANALIZ.md`, `README.md` son mimariye göre güncellenir.
- `CHANGELOG.md`'ye her fazın gerekçeli girişi eklenir (repo deseni).

**Kabul:** `dotnet build -p:GenerateDocumentationFile=true` uyarısız geçiyor (kırık `<see cref>` yok);
tablodaki her madde kodda hâlâ bulunabiliyor.

---

## Sıra ve bağımlılıklar

| # | Faz | Bağımlılık | Neden bu sırada |
|---|---|---|---|
| 0 | Token ölçümü | yok | Etkisi ölçülemeyen iyileştirme yapılmaz |
| 1 | Ajan ömrü | yok | Bağımsız, düşük riskli, sınırsız büyümeyi durdurur |
| 2 | Katalog + indeks seam'i | yok | Tak-çalıştır kesimini tamamlar |
| 3 | Ucuz tur azaltma | FAZ 0 | İŞ 6'nın üçte ikisi zaten yapılmış; hızlı kazanç |
| 4 | Redis: depo + kilit | yok (paket ekler) | En riskli faz; ucuz kazançlardan sonra |
| 5 | Redis önbellekleri | FAZ 2, FAZ 4 | Redis bağlantısına dayanır |
| 6 | Geçmiş kırpma + bağlam enjeksiyonu | FAZ 0, FAZ 5.2 | Keşif gerektirir, bloke olabilir |
| 7 | Çıkarım katmanı | FAZ 0 | Kod değişikliği yok, en son ayarlanır |
| 8 | Çok örnekli dağıtım | FAZ 4 + banka idempotency'si | Öncesinde güvenli değil |
| 9 | Dokümantasyon | Hepsi | Kod oturduktan sonra, bir kez yazılır |

---

## Fonksiyonel doğrulama listesi (her fazdan sonra)

| # | Ne | Beklenen | Hangi faz |
|---|---|---|---|
| 1 | `GET /api/diag` | 5 uzman, orkestratör hazır, MCP araç sayısı > 0 | 1 |
| 2 | `AgentIdleTimeoutMinutes: 1`, bekle, tekrar sor | Tahliye izi düşüyor, sonraki istek doğru yanıt veriyor | 1 |
| 3 | Aynı senaryo `UseA2AForSpecialists: false` ile | Aynı davranış — iki mod ayrışmıyor | **1.0** |
| 4 | Deklaratif uzman kaydet, bir tur bekle | Uzman görünür, orkestratör gereksiz yeniden kurulmuyor | **1.3** |
| 5 | *"Hesap özeti ekranını aç"* | İzde `POST /api/mkcodes` görünüyor | 2 |
| 6 | Bankayı kapat → host'u başlat → aç → ara | Açılış durmuyor; ilk aramada indeks doluyor | 2 |
| 7 | `Agents:Models:Orkestratör` küçük modele | Orkestratör turları o modelde, uzman turları büyükte | 3 |
| 8 | Ödeme akışını onaya getir → **host'u yeniden başlat** → onayla | İşlem tamamlanıyor | 4 |
| 9 | `LoadTest --mix double=100` (Redis açık ve kapalı) | Tek makbuz, `DOUBLE_CHARGE` yok | 4 |
| 10 | `LoadTest --mix pay=100` | Normal tamamlanıyor (iç içe scope bozulmamış) | 4 |
| 11 | Aynı soruyu iki kez sor | İkincisinde banka isteği azalıyor | 5 |
| 12 | Ödeme sonrası bakiye sor | **Güncel** değer dönüyor | 5 |
| 13 | 8+ turluk sohbet | Son turun girdi token'ı düşmüş; ödeme akışı hâlâ onaya ulaşıyor | 6 |
| 14 | `Agents:SelfBaseUrl` portunu değiştir | Sistem yeni portta; MCP + A2A kopmuyor | (regresyon) |
| 15 | `dotnet sln remove Banking.BankApi` → build Host | Derleniyor | (her faz) |

**Regresyon riski en yüksek dört nokta:**

- **Adım 3** — 1.0 yapılmazsa süreç içi ve A2A modu ayrışır, tahliye ölçülemez.
- **Adım 4** — 1.3 yapılmazsa tahliye yeniden kurulum fırtınası üretir.
- **Adım 9** — 4.3'teki iki serileştirme tuzağından biri atlanırsa çift tahsilat koruması sessizce
  bozulur. Parmak izi testi bunun tek erken uyarısıdır.
- **Adım 6** — `_index.Count == 0` yeniden deneme kuralı bozulursa arama kalıcı olarak sessiz kalır.

---

## Kapsam dışı ve dokunulmayacaklar

**Kapsam dışı:** kimlik doğrulama ve müşteri kimliğinin isteğe bağlanması. Mevcut kurgu korunur:
müşteri kimliği MCP araçlarının imzasında bulunmaz, sunucu tarafında `CustomerContext`'ten enjekte
edilir, sahiplik denetimi bankada yapılır. Önbellek anahtarları kimliği bu kaynaktan okur.

**Performans veya sadelik gerekçesiyle değiştirilmeyecekler** — her biri bir güvenlik veya doğruluk
sınırıdır:

- `ResponseSanitizer` — her yanıt buradan geçer. Streaming eklenirse ham çıktı akıtılamaz; tamamlanmış
  satır biriktirilip sanitize edildikten sonra gönderilir. Prompt'a yeni XML etiketi eklenirse
  `XmlTagPattern` ve `XmlBlockPattern`'e de eklenir.
- Onay kapısı (HITL) ve `WorkflowEngine.ApplyApprovalDecisionAsync` — para hareketi yalnızca açık
  onaydan sonra.
- MCP araçlarının imzasında müşteri kimliği bulunmaması.
- Bankanın ön doğrulama ile tahsilatı aynı kilit altında çalıştırması.
- `ToolRegistry.ValidateDiscoveredMcpTools` başlangıç fail-fast'i ve `ToolRegistry.Instance`'ın tek
  erişim yolu olması.
- `ISessionStore`'un tek çağıranının `AmbientSessionAccessor` olması; istek başına bir okuma, bir yazma.
- Derlenmiş uzman kartları — YAML'a taşınmaz; kartın eksik olamaması uzmanın sessizce kaybolmasını
  yapısal olarak engeller.
- `SpecialistCatalog.Version` semantiği — "katalog içeriği değişti" demektir; önbellek tahliyesi buna
  dâhil değildir.
- `Banking.Domain` ve `Banking.Host`'un `Banking.BankApi`'ye referans vermemesi.
