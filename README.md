# Bankacılık Asistanı — MAF Agentic Architecture (.NET 9)

> **Microsoft Agent Framework (MAF)**, **Model Context Protocol (MCP)** ve **Agent-to-Agent (A2A)** standartları ile geliştirilmiş; **deterministik iş akışı (Workflow)**, **izole araç yönetimi (Tool Registry)** ve **6 katmanlı fintech güvenlik süzgeci (Output Guard)** içeren kurumsal bankacılık asistanı.

---

## 1. Mimari Genel Bakış ve Çalışma Prensibi

Sistem, kullanıcı sorularını karşılayan bir **Orkestratör Ajan**, uzmanlık alanlarına göre ayrılmış **Uzman Ajanlar** (Hesap, Kredi Kartı, Finansal Analiz), veriye güvenli erişen **MCP Araçları** ve borç ödeme gibi kritik akışları yöneten **MAF Workflows** bileşenlerinden oluşur.

```
 Tarayıcı (Web UI) ──/api/chat──►  GatewayService (oturum scope'u + ResponseSanitizer)
                                        │
                                   FlowRouter (Deterministik Yönlendirici)
                                   ┌────┴──────────────────────────┐
                       (Aktif Akış YOK)                        (Aktif Akış VAR)
                               │                                   │
                               ▼                                   ▼
                      Orkestratör Ajan                       WorkflowEngine
                     (AgentRuntime & A2A)                 (evre durum makinesi)
                               │                                   │
              ┌────────────────┼────────────────┐                  │
              ▼                ▼                ▼                  ▼
         HesapUzmani    KrediKartiUzmani   AnalizUzmani     RequestPort (HITL Gate)
              │                │                │                  │
              └───────────────►├◄───────────────┘                  ▼
                               ▼                          WorkflowProcessor
                         Banking.Mcp                              │
                        (ToolRegistry)                            │
                               │                                  │
                               └──────────►  IBankApiClient  ◄─────┘
                                              (HTTP)
                                                 │
                                                 ▼
                                        Banka REST uçları
```

Sistem bankanın **üstüne eklenen bir katmandır**; bankaya giden her yol `IBankApiClient` üzerinden HTTP'dir. Bankanın kendi servislerine, modellerine veya veritabanına süreç içi hiçbir bağımlılık yoktur.

---

## 2. Proje Yapısı

| Proje | Sorumluluk |
|---|---|
| `src/Banking.Domain` | Bankadan bağımsız ortak parçalar: sabitler, `MkCodeDefinition`, tek JSON politikası (`JsonPolicy`) ve `Semantic/` — tek vektör arama uygulaması `SemanticIndex<T>` ile kalıcılık seam'i `ISemanticIndexStore`. **Hiçbir projeye referansı yoktur.** |
| `src/Banking.BankApi.Client` | `IBankApiClient` — sistemin bankaya açılan **tek kapısı**. Idempotency başlığı, kimlik başlığı, hata çıkarımı ve çağrı izleme burada. |
| `src/Banking.Mcp` | MCP Server araçları (`AccountsTools`, `CardsTools`, `AnalyzeTools`, `LoansTools`, `PaymentsTools`, `InvestmentsTools`, `CustomerTools` — 42 araç), otomatik taranan `ToolRegistry`, araçların bankaya tek çıkışı `BankApiToolGateway`, `CustomerContext` ve bankadan beslenen semantik arayıcılar (`Semantic/`: ekran kodu, kampanya, finansman). |
| `src/Banking.Agents` | MAF ajan altyapısı: `Runtime/` (orkestratör), `Sessions/` (oturumun tek kapısı), `Workflows/` (motor, slotlar, onay grafiği, deklaratif tanımlar), `Specialists/`, `Prompts/`, `Validation/` ve `Tracing/`. |
| `src/Banking.Gateway` | REST API Gateway, `FlowRouter`, `GatewayService` ve 6 katmanlı `ResponseSanitizer` (Output Guard). |
| `src/Banking.Host` | ASP.NET Core Composition Root (`Program.cs`, `Composition/`), MCP/A2A endpoint hostlama, deklaratif tanımların disk kalıcılığı (`declarative-agents/`, `specialist-agents/`) ve Web UI. |

`tools/WorkflowTest` (LLM'siz senaryo koşucusu), `tools/LoadTest` (uçtan uca yolculuk ve sözleşme denetimleri) ve `tools/ApiDump` (kurulu MAF/MCP/A2A API yüzeyini dökme) çalışma zamanının parçası değildir.

---

## 3. Microsoft Agent Framework (MAF) Built-in Fonksiyon Entegrasyonu

Sistem, Microsoft'un resmi AI ve Agent kütüphanelerinin **yerleşik (built-in) primitive ve API'ları** kullanılarak inşa edilmiştir:

### 1. `Microsoft.Agents.AI` (Ajan ve Hafıza Yönetimi)
- **`AIAgent` & `AgentSession`:** Uzman ajanlar (`HesapUzmani`, `KrediKartiUzmani`, `FinansalAnalizUzmani`) `IChatClient.AsAIAgent()` yerleşik metodu ile MAF ajanlarına dönüştürülür. Konuşma geçmişi ve durum MAF'ın kendi yerleşik `AgentSession` yapısı tarafından yönetilir.

### 2. `Microsoft.Agents.AI.Workflows` (İş Akışları ve HITL Gates)
- **`WorkflowBuilder` & `Workflow`:** Onay grafiği yerleşik `WorkflowBuilder` ile grafik düğümleri (nodes & edges) olarak inşa edilir (`DeterministicActionWorkflow`).
- **`Executor<TInput, TOutput>`:** Onay sonrası çalışan eylem adımı (`DeterministicActionStep`) MAF'ın yerleşik `Executor` sınıfından türetilmiştir.
- **`RequestPort.Create<TInput, TOutput>()`:** Onay gerektiren durumlarda insan müdahalesi (HITL) için MAF'ın yerleşik `RequestPort` onay kapıları kullanılır. Kapının kurulup kurulmayacağını YAML'daki `action.path` belirler.
- **`InProcessExecution` & event akışı:** Grafik tek HTTP isteği içinde çalıştırılır; çıktı ve hata olayları (`WorkflowOutputEvent`, `ExecutorFailedEvent`) yerleşik event akışından okunur. Sunucuda askıda çalıştırma tutulmaz, yapışkan oturum gerekmez.

### 3. `Microsoft.Agents.AI.A2A` (Agent-to-Agent Protokolü)
- Orkestratör ajanın uzman ajanlarla konuşması HTTP loopback üzerinde yerleşik A2A (JSON-RPC) protokolü ile gerçekleştirilir (`/a2a/hesap`, `/a2a/kart`).

### 4. `Microsoft.Extensions.AI` (Abstrasksiyon ve MCP)
- LLM model entegrasyonu `IChatClient`, `ChatMessage` ve `AITool` standart arabirimleriyle sağlanır.
- MCP Server entegrasyonu `ModelContextProtocol.Server` paketi ve `[McpServerTool]`, `[McpServerToolType]` öznitelikleriyle yürütülür.

---

## 4. Bu Sistemi Öne Çıkaran Yetenekler

### Yeni iş akışı yazmak, kod yazmak değildir
Sistemde **tek bir iş akışı motoru** vardır; akıştan akışa değişen tek şey YAML dosyasıdır. C# record'u, akışa özel executor, attribute ya da DI kaydı yoktur. Tanım `/api/workflows/register-declarative` ile **çalışırken** yüklenir, anında semantik indekse girer ve o an yönlendirilebilir hâle gelir — yeniden derleme yok, soğuk başlangıç yok. Bugün 13 iş akışı ve 6 uzman ajan tam olarak bu şekilde, tek satır C# yazılmadan tanımlıdır. Yeni bir MCP aracı da aynı ölçüde ucuzdur: `[McpServerTool]` işaretli bir metot.

### Para hareket ederken kontrol modelde değildir
Müşteri bir ödeme akışının içindeyken mesajlar orkestratöre **hiç gönderilmez**; `FlowRouter` bu kararı aktif akış bayrağına bakarak verir, modele sorarak değil. Akışın hangi uç noktayı çağıracağı, onay kapısının kurulup kurulmayacağı, slotların tamamlanıp tamamlanmadığı ve bankanın ne zaman çağrılacağı — bunların hiçbiri modelin elinde değildir. Akıştan çıkış yolları da cümle değil **araç sonucudur** (`cancel_*`, `handoff_*`); onaya giden değerler ajanın yazdığı metinden değil, oturuma tek tek yazılmış slotlardan okunur.

Modelin elinde olan tek şey slot **değerleridir** — ve orada hata payı vardır. Şema doğrulaması tipi denetler, bankacılık kuralını değil: sistem bilinçli olarak "bakiyeyi aşan tutar" gibi kuralları kendi içinde ikinci kez tanımlamaz. Hatalı bir değeri yakalayan üç süzgeç vardır: tip denetimi, kullanıcının onaylamadan önce gördüğü **onay ekranı** ve bankanın kendi doğrulaması. Yani model yanlış bir tutar üretebilir; o tutarın kullanıcı görmeden ve banka onaylamadan hesaptan çıkması mümkün değildir.

### Onaysız hiçbir kuruş hareket etmez (HITL)
Finansal etkisi olan her eylem MAF `RequestPort` onay kapısından geçer: grafik durur, kullanıcı ekrandan açıkça onaylar, ancak ondan sonra banka çağrılır. Kapının kurulup kurulmayacağına akışın adı değil, YAML'ın banka ucu bildirip bildirmediği karar verir — banka çağırmayan bir akış kullanıcıya sahte bir onay ekranı göstermez.

### Oturum yönetimi: tek kapı, tek okuma, tek yazma
Oturum durumuna erişen her katman `SessionContext` üzerinden geçer. Durum istek başına **bir kez** yüklenir, scope kapanışında **bir kez** yazılır; arada okuma/yazma yoktur — yani iki katmanın birbirinin yazdığını sessizce ezmesi mümkün değildir. Scope açılışında oturum başına kilit alınır: "Onayla" butonuna iki kez basmak, idempotency anahtarıyla birlikte eylemin **tam olarak bir kez** çalışmasını garanti eder. Ambient scope sayesinde oturum kimliği hiçbir araç parametresinde taşınmaz; bir oturumun içine farklı bir oturum yerleştirilmesi ise doğrudan reddedilir — bir müşterinin verisi diğerine ulaşamaz. Kalıcılık tek bir arabirimin (`ISessionStore`) arkasındadır: çok örnekli dağıtım için Redis'e geçiş belgelenmiş **tek satırlık** bir değişikliktir.

### Yanıt kullanıcıya çıkmadan süzgeçten geçer
Tüm yanıtlar tek bir çıkış noktasından (`GatewayService.FinalizeResponse`) geçer ve 6 katmanlı `ResponseSanitizer` tarafından temizlenir: iç araç adları, düşünce (CoT) adımları, XML prompt etiketleri, sızmış sistem promptu parçaları, iç adresler ve oturum token'ları. Bu, modelin talimata uyup uymadığından **bağımsız** deterministik bir güvenlik sınırıdır.

### Her ajan yalnızca kendi araçlarını görür
Uzman ajanlara sadece yetkili oldukları araç grubu sunulur (`Account`, `Card`, `Analyze`, `Loan`, `Payment`, `Investment`, `Customer`). Bu hem token maliyetini düşürür hem de yanlış araç tetikleme yüzeyini sıfırlar. Araçlar assembly taramasıyla otomatik keşfedilir; kayıt ile canlı MCP sunucusunun araç listesi uyuşmazsa uygulama **açılışta** hata verir — hatalı bir dağıtım çalışır hâle hiç gelmez.

### Ölçekleme baştan düşünülmüştür
Uzman ajanlar başlangıçta değil ilk istekte kurulur, boşta kalanlar tahliye edilir; her model turu token maliyetiyle birlikte ölçülür ve canlı iz akışına (`/api/trace/stream`) yazılır. Ajan başına farklı model kullanmak bir **konfigürasyon satırıdır**, kod dalı değil — ucuz model ucuz işe, güçlü model kritik işe, derlemeden.

### Tak-çalıştır konuşlandırma
Gerçek ortama geçmek için dokunulan yer dörttür: `appsettings.json` içindeki `BankApi` bölümü, uç nokta yolları (`BankApiPaths.cs`), akış YAML'larındaki `action.path` ve semantik özet kayıtlarının alan eşlemesi. Uygulama akışının hiçbir parçası yeniden yazılmaz.

---

## 5. Hızlı Başlangıç

### Gereksinimler
- **.NET 9 SDK**
- **OpenAI uyumlu bir LLM endpoint'i** — bulut ya da kurum içi bir model sunucusu. Adres `Agents:Endpoint`, model adı `Agents:Model` ile verilir; adres boş bırakılırsa `api.openai.com` kullanılır. Model **araç çağrısını (tool calling) desteklemelidir**.
- **API anahtarı** — `Agents:ApiKey` veya `OPENAI_API_KEY` ortam değişkeni. Kimlik doğrulaması istemeyen kurum içi sunucularda boş bırakılabilir.
- **Erişilebilir bir banka REST API'si** — `BankApi:BaseUrl`.

### Çalıştırma
```powershell
# 1. API anahtarı (sunucu anahtar istemiyorsa atlanabilir)
$env:OPENAI_API_KEY = "sk-..."

# 2. Uygulamayı başlatın
dotnet run --project src/Banking.Host
```

Tarayıcıdan erişin: **`http://localhost:5080`**

Port `appsettings.json` içindeki `Agents:SelfBaseUrl` değerinden türetilir; başka hiçbir yerde port yazılı değildir.

### Yapılandırma — dağıtımda dokunulacak anahtarlar

Hepsi `src/Banking.Host/appsettings.json` içindedir ve her birinin yanında açıklama satırı (`*COMMENT`) durur.

| Anahtar | Ne işe yarar |
|---|---|
| `Agents:Endpoint` | **Modelin adresi.** Boşsa `api.openai.com`; kurum içi model sunucusu için OpenAI uyumlu taban adres (`http://<host>:<port>/v1`). |
| `Agents:Model` | Endpoint'in servis ettiği model adı. Araç çağrısını desteklemelidir. |
| `Agents:ApiKey` | **Dosyaya yazılmaz.** User Secrets → `OPENAI_API_KEY` sırasıyla okunur. Dosyaya boş bir `ApiKey` satırı eklemek ortam değişkenine düşüşü kapatır. |
| `Agents:SelfBaseUrl` | **Uygulamanın kendi adresi** — modelin değil. Sunucu bu portu dinler, MCP/A2A çağrıları da buraya döner. |
| `Agents:Models` | İsteğe bağlı `ajan etiketi → model` haritası. Boş bırakılırsa tüm ajanlar `Agents:Model` kullanır. |
| `BankApi:BaseUrl` | Bankanın adresi. |
| `BankApi:ApiKey` | Bankaya gönderilen `X-Api-Key` başlığı. Boşsa başlık eklenmez. |
| `BankApi:DefaultCustomerId` | İsteği yapan müşterinin kimliği. Sessiz varsayılanı yoktur; tanımsızsa istekler hata döner. |

### LLM'siz doğrulama

```powershell
dotnet run --project tools/WorkflowTest   # HITL onay/ret/çift tıklama senaryoları
dotnet run --project tools/LoadTest       # çalışan sunucuya karşı uçtan uca yolculuklar
```

---

## 6. Eklenmesi Planlanan Özellikler

Aşağıdaki üç madde de mimaride **bilinçli olarak açık bırakılmış boşluklara** oturuyor: hiçbiri akışın yeniden yazılmasını gerektirmez, üçü de geriye dönük uyumludur — bugünkü YAML tanımları olduğu gibi çalışmaya devam eder.

### Dağıtık oturum deposu (Redis) — tek DI satırıyla yatay ölçekleme

Oturum durumu bugün bellek içinde tutuluyor (`InMemorySessionStore`, kayan TTL). Bu, tek süreçte kusursuz çalışır ama uygulama iki kopyaya çıktığında kullanıcının yarım kalmış ödemesi diğer kopyada görünmez.

Çözüm **yeni bir mimari değil, var olan bir arayüzün ikinci uygulaması**: `ISessionStore` tam olarak bu gün için tasarlandı — metot imzaları ağ gecikmesini karşılamak üzere `ValueTask` döndürüyor ve tek çağıranı `AmbientSessionAccessor`. Uygulama kodunun hiçbir yeri oturum deposunu doğrudan çağırmadığı için değişecek yer **bir sınıf ve bir DI kaydından ibaret**.

Bunun bu kadar ucuz olmasının nedeni oturum disiplininin baştan buna göre kurulmuş olması: durum istek başına **bir kez** okunur, scope kapanışında **bir kez** yazılır. Ara okuma/yazma olmadığı için Redis'e giden çağrı sayısı da istek başına ikidir — dağıtık depo, sohbet başına onlarca round-trip'e dönüşmez.

Kazanım tabloya dökülürse:

| Bugün | Redis ile |
|---|---|
| Tek süreç; ikinci kopya oturumu göremez | Kopya sayısı serbest, oturum ortak |
| Yeniden başlatma yarım akışları düşürür | Akış pod yeniden başlasa da kaldığı yerden devam eder |
| Yük dengeleyicide yapışkan oturum şart | **Yapışkan oturum gerekmez** |

Son satır önemli: HITL onay grafiği zaten **tek bir HTTP isteği içinde** kurulup çalıştırılıyor, sunucuda askıda bekleyen bir çalıştırma bırakılmıyor. Yani mimari yapışkan oturuma bugün de ihtiyaç duymuyor; Redis bu resmi tamamlıyor.

Aynı hamle ikinci bir seam'i de açıyor: `ISemanticIndexStore`. Vektör indeksleri de Redis'e alındığında N kopya aynı katalogu N kez gömmek yerine tek bir paylaşılan indeksi okur — açılış süresi ve gömme maliyeti kopya sayısıyla birlikte artmayı bırakır.

### Kısıtlı seçim: "yalnızca bunlardan birini seçebilirsin"

Bugün YAML şeması bir slot için tipi, zorunluluğu ve açıklamasını taşıyor. Taşımadığı şey **izin verilen değerler kümesi**. Yani "hesap türü" alanına modelin `vadeli`, `Vadeli Mevduat` ya da `tasarruf` yazmasını ayıran deterministik bir kural yok; alan `string` olduğu için üçü de geçerli.

Planlanan ek, şema alanına bir değer listesi koymak:

```yaml
outputSchema:
  type: object
  properties:
    hesapTuru:
      type: string
      required: true
      description: Açılacak hesabın türü
      allowedValues: [vadeli, vadesiz, altin]   # ← yeni
```

Bu tek satır sistemin **üç ayrı katmanında birden** karşılık buluyor ve asıl değeri bu:

1. **Deterministik kapı.** Listede olmayan değer `SchemaValidatedSlotWriter` tarafından oturuma yazılmadan reddedilir — modelin talimata uyup uymamasından bağımsız olarak. Kategorik alanlarda uydurma yüzeyi sıfıra iner; yanlış bir "hesap türü" bankaya ulaşamaz.
2. **Ajanın sorusu netleşir.** Beklenen değerler, tip bilgisinin bugün de aktığı kanaldan (`update_slot` araç açıklaması) modele ulaşır. Ajan "hesap türünü söyler misiniz?" yerine **"Vadeli mi, vadesiz mi, altın mı?"** diye sorar — tur sayısı düşer, kullanıcı ne yazacağını tahmin etmek zorunda kalmaz.
3. **Arayüz gerçek bir seçim kutusuna dönüşür.** Hem onay ekranı hem de akış oluşturma formu listeyi olduğu gibi render eder.

Formda da karşılığı olacak: parametre satırına "izin verilen değerler" girişi eklenir ve girilen liste üretilen YAML'a otomatik işlenir — akışı arayüzden tanımlayan kişinin YAML sözdizimi bilmesi gerekmez.

### Tip sistemi: Yetersiz kalmakta

Bugün doğrulama kapısı JSON'un kaba tiplerini ayırt ediyor: `string`, `number`, `boolean`, `object`, `array`. Bunun iki pratik sonucu var ve ikisi de bir fintech için fazla toleranslı:

- **`integer` ile `number` arasında fark yok.** `taksitSayisi` alanı `integer` bildirilse bile `3.5` değeri kapıdan geçer.
- **Tanınmayan tip = kısıtsız alan.** YAML'a bugün `decimal` ya da `guid` yazarsan hata almazsın — ama **hiçbir kısıt da uygulanmaz.** Arayüzün tip listesinde duran "Tarih" seçeneği de bu durumda: seçilebiliyor, henüz zorlanmıyor.

Planlanan genişleme, tipi kaba bir JSON sınıfı olmaktan çıkarıp gerçek bir sözleşmeye dönüştürüyor:

| Tip                 | Nerede işe yarar                             | Neyi engeller                                                            |
|---------------------|----------------------------------------------|--------------------------------------------------------------------------|
| `int`, `long`       | Taksit sayısı, vade ayı, adet                | Ondalıklı değerin tam sayı alanına yazılması                             |
| `decimal`           | **Para tutarları**                           | İkili kayan noktanın yuvarlama hatası; tutarın `double` olarak taşınması |
| `double` / `float`  | Oran, faiz, puan                             | Hassasiyet gerektirmeyen alanların gereksiz `decimal` maliyeti           |
| `guid`              | `cardId`, `sourceAccountId` gibi kimlikler   | Biçimi bozuk kimliğin bankaya kadar gitmesi                              |
| `date` / `datetime` | Vade tarihi, talimat günü                    | Serbest metin tarihin uca ulaşması                                       |
| `özel beklenen`     | Backend tarafında beklenen özel veri tipleri | Backend ile uyumlu olmayan değerlerin reddedilmesi                       |

En çok kazandıran ikisi:

**`decimal` — para için doğru tip.** Tutar alanının `number` olması, değerin yol boyunca kayan noktalı sayıya dönüşmesini mümkün kılıyor; ikili kayan nokta ise `0.1 + 0.2` gibi basit toplamlarda bile tam sonuç vermez. Para alanını YAML'da `decimal` bildirmek, hem niyeti açıkça yazılı hâle getirir hem de kapının bunu **zorlamasını** sağlar — tutar bankaya giden gövdeye kadar hassasiyet kaybetmeden gider.

**`guid` — hata bankaya gitmeden yakalanır.** Bugün biçimi bozuk bir kart kimliği ancak bankanın yanıtında hata olarak görünür: kullanıcı bir tur kaybeder, izlerde banka hatası birikir. Tip kapısı bunu **aynı turda** yakalar; ajan hatalı değeri reddeden mesajı alır ve kullanıcıya doğru alanı yeniden sorar. Kullanıcı bir banka hatası değil, bir soru görür.

Doğrulamanın nerede yapıldığı değişmiyor — kural yine tek bir yerde, `SchemaValidatedSlotWriter` içinde duruyor; genişleyen tek şey tanınan tip kümesi. Ve bu genişleme bilinçli olarak **şema doğrulamasıyla sınırlı**: yetersiz bakiye, borcu aşan tutar ya da IBAN'ın gerçekten var olup olmadığı gibi bankacılık kuralları bankanın uçlarına ait kalmaya devam eder. Sistem bankanın kurallarını ikinci kez tanımlamaz.

---

## 7. Detaylı Mimari ve Tasarım Dokümanı

Projenin tüm sınıf detayları, metot açıklamaları, kararları içeren eksiksiz Mermaid akış şeması ve uçtan uca senaryolar için [ANALIZ.md](ANALIZ.md) dokümanını inceleyebilirsiniz.
