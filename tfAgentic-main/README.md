# Bankacılık Asistanı — MAF Agentic Architecture (.NET 9)

> **Microsoft Agent Framework (MAF)**, **Model Context Protocol (MCP)** ve **Agent-to-Agent (A2A)** standartları ile geliştirilmiş; **deterministik iş akışı (Workflow)**, **izole araç yönetimi (Tool Registry)** ve **6 katmanlı fintech güvenlik süzgeci (Output Guard)** içeren kurumsal bankacılık asistanı.

---

## 1. Mimari Genel Bakış ve Çalışma Prensibi

Sistem, kullanıcı sorularını karşılayan bir **Orkestratör Ajan**, uzmanlık alanlarına göre ayrılmış **Uzman Ajanlar** (Hesap, Kredi Kartı, Finansal Analiz), veriye güvenli erişen **MCP Araçları** ve borç ödeme gibi kritik akışları yöneten **MAF Workflows** bileşenlerinden oluşur.

```
 Tarayıcı (Web UI) ──/api/chat──►  GatewayService (ResponseSanitizer)
                                        │
                                   FlowRouter (Deterministik Yönlendirici)
                                   ┌────┴──────────────────────────┐
                       (Aktif Oturum YOK)                      (Aktif Oturum VAR)
                               │                                   │
                               ▼                                   ▼
                      Orkestratör Ajan                     MAF Workflow
                     (AgentRuntime & A2A)             (GenericSlotFillingStep)
                               │                                   │
              ┌────────────────┼────────────────┐                  │
              ▼                ▼                ▼                  ▼
         HesapUzmani    KrediKartiUzmani   FinansalAnaliz   RequestPort (HITL Gate)
              │                │                │                  │
              └───────────────►├◄───────────────┘                  ▼
                               ▼                            Charge() Servisi
                         Banking.Mcp
                      (IToolRegistry)
                               │
                               ▼
                        Banking.Domain
```

---

## 2. Neden Dinamik Bir Yapı Kullanıldı?

Proje geliştirilirken statik, katı ve hardcoded yapılar yerine **tam dinamik bir mimari** tercih edilmiştir. Bunun temel nedenleri şunlardır:

1. **Dinamik Slot Filling & Workflow Adapte Olabilirliği:** 
   Ödeme, havale veya kredi başvurusu gibi akışlarda her işlem türü farklı parametreler (`PaymentSlots`, `TransferSlots` vb.) gerektirir. Sınıflar hardcode yazılmak yerine **Reflection tabanlı `GenericSlotFillingStep<TSlots>`** olarak tasarlandı. Bu sayede sisteme yeni bir `record` eklendiğinde hiçbir akış kodunu değiştirmeden sistem dinamik olarak slot doldurabilir.
2. **Dynamic Tool Registry & Auto-Discovery:** 
   MCP araçlarının ve yetki alanlarının elle C# kodlarına gömülmesi yerine `IToolRegistry` ve `ToolGroupAttribute` mimarisi kuruldu. Assembly üzerindeki tüm araçlar uygulama başlarken otomatik taranır ve gruplanır.
3. **Dinamik Context Enjeksiyonu (TopicContextProvider):** 
   Sistem prompt'larının sabit metinler olması modelin odak kaybetmesine yol açar. `TopicContextProvider` sayesinde kullanıcının anlık sohbet konusuna göre sistem talimatları dinamik olarak üretilir ve modele aktarılır.
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
  Sisteme yeni bir bankacılık aracı, uzman ajan veya iş akışı eklemek yalnızca yeni bir sınıf ve `[ToolGroup]` etiketi eklemek kadar kolaydır.

---

## 4. Microsoft Agent Framework (MAF) Built-in Fonksiyon Entegrasyonu

Sistem, Microsoft'un resmi AI ve Agent kütüphanelerinin **yerleşik (built-in) primitive ve API'ları** kullanılarak inşa edilmiştir:

### 1. `Microsoft.Agents.AI` (Ajan ve Hafıza Yönetimi)
- **`AIAgent` & `AgentSession`:** Uzman ajanlar (`HesapUzmani`, `KrediKartiUzmani`, `FinansalAnalizUzmani`) `IChatClient.AsAIAgent()` yerleşik metodu ile MAF ajanlarına dönüştürülür. Konuşma geçmişi ve durum MAF'ın kendi yerleşik `AgentSession` yapısı tarafından yönetilir.

### 2. `Microsoft.Agents.AI.Workflows` (İş Akışları ve HITL Gates)
- **`WorkflowBuilder` & `Workflow`:** İş akışları yerleşik `WorkflowBuilder` ile grafik düğümleri (nodes & edges) olarak inşa edilir.
- **`Executor<TInput, TOutput>`:** Slot toplama adımları (`GenericSlotFillingStep<TSlots>`) MAF'ın yerleşik `Executor` sınıfından türetilmiştir.
- **`RequestPort.Create<TInput, TOutput>()`:** Onay gerektiren durumlarda insan müdahalesi (HITL) için MAF'ın yerleşik `RequestPort` onay kapıları kullanılır.
- **`IWorkflowContext`:** Adımlar arası durum aktarımı MAF'ın `QueueStateUpdateAsync` ve yerleşik context API'ları ile sağlanır.

### 3. `Microsoft.Agents.AI.A2A` (Agent-to-Agent Protokolü)
- Orkestratör ajanın uzman ajanlarla konuşması HTTP loopback üzerinde yerleşik A2A (JSON-RPC) protokolü ile gerçekleştirilir (`/a2a/hesap`, `/a2a/kart`).

### 4. `Microsoft.Extensions.AI` (Abstrasksiyon ve MCP)
- LLM model entegrasyonu `IChatClient`, `ChatMessage` ve `AITool` standart arabirimleriyle sağlanır.
- MCP Server entegrasyonu `ModelContextProtocol.Server` paketi ve `[McpServerTool]`, `[McpServerToolType]` öznitelikleriyle yürütülür.

---

## 5. Proje Yapısı

| Proje | Sorumluluk |
|---|---|
| `src/Banking.Domain` | Modeller, in-memory banka veri deposu, hesap/kart/tahsilat servisleri ve vektör arama motorları (`SemanticFinancingSearcher`, `SemanticCampaignSearcher`). |
| `src/Banking.Mcp` | MCP Server araçları (`AccountsTools`, `CardsTools`, `AnalyzeTools`) ve otomatik taranan `ToolRegistry` mimarisi. |
| `src/Banking.Agents` | MAF Uzman Ajanları (`SpecialistAgents`), `AgentRuntime`, `AgentPrompts`, `TopicContextProvider`, `CollectionWorkflow` ve `TraceHub`. |
| `src/Banking.Gateway` | REST API Gateway, `FlowRouter`, `GatewaySession` ve 6 katmanlı `ResponseSanitizer` (Output Guard). |
| `src/Banking.Host` | ASP.NET Core Composition Root (`Program.cs`), MCP/A2A endpoint hostlama ve Markdown destekli Web UI. |

---

## 6. Hızlı Başlangıç ve Çalıştırma

### Gereksinimler
- **.NET 9 SDK**
- **OpenAI API Key** (`OPENAI_API_KEY`)

### Çalıştırma
```powershell
# 1. API Anahtarını Tanımlayın (PowerShell)
$env:OPENAI_API_KEY = "sk-..."

# 2. Uygulamayı Başlatın
dotnet run --project src/Banking.Host
```

Tarayıcıdan erişin: **`http://localhost:5080`**

---

## 7. Detaylı Mimari ve Tasarım Dokümanı

Projenin tüm sınıf detayları, metot açıklamaları, kararları içeren eksiksiz Mermaid akış şeması ve uçtan uca senaryolar için [ANALIZ.md](ANALIZ.md) dokümanını inceleyebilirsiniz.
