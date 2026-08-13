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
                               ▼                          RestWorkflowExecutor
                         Banking.Mcp                              │
                        (ToolRegistry)                            │
                               │                                  │
                               └──────────►  IBankApiClient  ◄─────┘
                                              (HTTP)
                                                 │
                                                 ▼
                                        Banka REST uçları
```

> **Not:** `src/Banking.BankApi` bu depodaki **geçici bir vekil bankadır** ve silinecektir. Sistemin amacı var olan bir bankaya agentic katman eklemektir; bankaya giden her yol `IBankApiClient` üzerinden HTTP'dir. Ayrıntı için `CLAUDE.md` → *Target architecture*.

---

## 2. Neden Dinamik Bir Yapı Kullanıldı?

Proje geliştirilirken statik, katı ve hardcoded yapılar yerine **tam dinamik bir mimari** tercih edilmiştir. Bunun temel nedenleri şunlardır:

1. **Yeniden derleme gerektirmeyen iş akışları:** 
   Yeni bir akış eklemek bir **YAML dosyası yazmaktan ibarettir** — C# record'u, akışa özel executor, attribute veya DI kaydı yoktur. `WorkflowEngine` sistemdeki tek motordur ve akıştan akışa değişen tek girdi YAML'daki `WorkflowActionDefinition`'dır. Tanım `/api/workflows/register-declarative` ile çalışma zamanında da yüklenebilir ve anında yönlendirilebilir hâle gelir.
2. **Dynamic Tool Registry & Auto-Discovery:** 
   MCP araçlarının ve yetki alanlarının elle C# kodlarına gömülmesi yerine `ToolRegistry` ve `ToolGroupAttribute` mimarisi kuruldu. Assembly üzerindeki tüm araçlar uygulama başlarken otomatik taranır ve gruplanır.
3. **Tek semantik indeks:** 
   Ekran kodu, iş akışı, kampanya ve finansman aramalarının tamamı tek bir `SemanticIndex<T>` üzerinde çalışır. Benzerlik eşikleri kayıt türüne göre farklıdır ve her biri gerekçesiyle belgelenmiştir; arama kodu kopyalanmaz.
4. **Runtime Fail-Fast Doğrulama:** 
   MCP Sunucusunda tanımlı olan araçlar ile kod içerisindeki kayıtlar uygulama başlarken (`ValidateDiscoveredMcpTools`) karşılaştırılır. Uyuşmayan veya eksik bir araç varsa sistem runtime anında çökmeden startup'ta anında hata (`InvalidOperationException`) vererek güvenliği garanti eder.

---

## 3. Bu Sistem Mimarisinin Avantajları Nelerdir?

* **%100 Determinizm ve Sıfır Hata (Orkestratör Bypass):** 
  Müşteri borç ödeme akışındayken mesajlar LLM Orkestratör'e gönderilmez. `FlowRouter` sayesinde doğrudan MAF Workflow'a iletilir. Bu sayede LLM'in adımları atlaması, konudan sapması veya hatalı tutar çekmesi tamamen engellenir.
* **Fintech Düzeyinde Güvenlik (6 Katmanlı Output Guard):** 
  `ResponseSanitizer` sınıfı sayesinde model yanıt verseler dahi iç araç adları (`GetCreditCardStatement`), CoT düşünce adımları, XML prompt etiketleri, localhost URL'leri ve session token'ları kullanıcıya sızdırılamaz.
* **Token ve Maliyet Tasarrufu (Tool Grouping):** 
  Uzman ajanlara sadece yetkili oldukları araç grubu (`Account`, `Card`, `Analyze`) sunulur. Bir hesap uzmanının ekstre analiz aracını görmesi engellenerek hem token israfı önlenir hem de yanlış araç tetikleme ihtimali sıfırlanır.
* **Human-in-the-Loop (HITL) Güvenliği:** 
  Finansal etkisi olan işlemler asla LLM tarafından doğrudan çalıştırılmaz. MAF `RequestPort` onay kapısına bağlanarak kullanıcının ekrandan butonla açık onayı (`CONFIRM_PAYMENT`) alınır.
* **Kolay Genişletilebilirlik:** 
  Yeni iş akışı = bir YAML dosyası. Yeni uzman = bir YAML dosyası ya da statik `AgentCard Card` özelliği taşıyan bir sınıf. Yeni MCP aracı = `[McpServerTool]` işaretli bir metot. Üçünde de yönlendirme kodu değişmez.
* **Tak-Çalıştır Konuşlandırma:** 
  Gerçek bankaya geçmek için dokunulan yer dörttür: `appsettings.json` içindeki `BankApi` bölümü, `BankApiPaths.cs`, akış YAML'larındaki `action.path` ve semantik özet kayıtlarının alan eşlemesi. Uygulama akışının hiçbir parçası yeniden yazılmaz.

---

## 4. Microsoft Agent Framework (MAF) Built-in Fonksiyon Entegrasyonu

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

## 5. Proje Yapısı

| Proje | Sorumluluk |
|---|---|
| `src/Banking.Domain` | Bankadan bağımsız ortak parçalar: sabitler, `MkCodeDefinition` ve `Semantic/` — tek vektör arama uygulaması `SemanticIndex<T>` ile kalıcılık seam'i `ISemanticIndexStore`. **Hiçbir projeye referansı yoktur.** Ekran kodu kaydı burada tutulmaz; kaynağı bankadır (`Banking.Mcp/Semantic/MkCodeCatalog`). |
| `src/Banking.BankApi.Client` | `IBankApiClient` — sistemin bankaya açılan **tek kapısı**. Idempotency başlığı, hata çıkarımı ve çağrı izleme burada. |
| `src/Banking.Mcp` | MCP Server araçları (`AccountsTools`, `CardsTools`, `AnalyzeTools`), otomatik taranan `ToolRegistry`, `CustomerContext` ve bankadan beslenen semantik arayıcılar (`Semantic/`: ekran kodu, kampanya, finansman — dördü de `ISemanticIndexStore` ile tekrar vektörleştirmeden kaçınır). |
| `src/Banking.Agents` | MAF ajan altyapısı: `AgentRuntime`, `SpecialistCatalog`, `WorkflowEngine`/`WorkflowRegistry`, `Sessions/` (oturumun tek kapısı), `AgentPrompts` ve `TraceHub`. |
| `src/Banking.Gateway` | REST API Gateway, `FlowRouter`, `GatewayService` ve 6 katmanlı `ResponseSanitizer` (Output Guard). |
| `src/Banking.Host` | ASP.NET Core Composition Root (`Program.cs`, `Composition/`), MCP/A2A endpoint hostlama, deklaratif tanımların disk kalıcılığı ve Web UI. |
| `src/Banking.BankApi` | **Geçici vekil banka.** Ayrı süreç (port 5001). Başka hiçbir proje ona referans vermez. |

---

## 6. Hızlı Başlangıç ve Çalıştırma

### Gereksinimler
- **.NET 9 SDK**
- **OpenAI API Key** (`OPENAI_API_KEY`)

### Çalıştırma
```powershell
# 1. API Anahtarını Tanımlayın (PowerShell)
$env:OPENAI_API_KEY = "sk-..."

# 2. Vekil bankayı başlatın (ayrı terminal, port 5001)
dotnet run --project src/Banking.BankApi

# 3. Uygulamayı Başlatın
dotnet run --project src/Banking.Host
```

Tarayıcıdan erişin: **`http://localhost:5080`**

Port `appsettings.json` içindeki `Agents:SelfBaseUrl` değerinden türetilir; başka hiçbir yerde port yazılı değildir.

### LLM'siz doğrulama

```powershell
dotnet run --project tools/WorkflowTest   # HITL onay/ret/çift tıklama senaryoları
```

---

## 7. Detaylı Mimari ve Tasarım Dokümanı

Projenin tüm sınıf detayları, metot açıklamaları, kararları içeren eksiksiz Mermaid akış şeması ve uçtan uca senaryolar için [ANALIZ.md](ANALIZ.md) dokümanını inceleyebilirsiniz.
