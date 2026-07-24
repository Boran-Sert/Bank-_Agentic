# Bankacılık Asistanı — MAF Agentic (.NET)

Microsoft Agent Framework (MAF) tabanlı, **tam agentic** bir bankacılık asistanı. Bir **orkestratör** ajan
soruları karşılar ve **A2A** üzerinden uzman ajanlara yönlendirir; uzman ajanlar bankacılık servislerine
**MCP** araçları üzerinden erişir. Ayrıca **human-in-the-loop** bir kredi kartı borç tahsilat akışı içerir:
ajan eksik bilgileri kendisi sorar, sonunda deterministik bir **onay ekranı** çıkar, kullanıcı onaylarsa
tahsilat servisi çağrılır.

> Tasarımın tamamı: [ANALIZ.md](ANALIZ.md) · Doğrulanmış MAF/MCP/A2A API notları: [docs/MAF-API-NOTES.md](docs/MAF-API-NOTES.md)

## Mimari özet

```
Tarayıcı ──/api/chat──►  ORKESTRATÖR ajan ──A2A──►  HesapUzmani ──MCP──►  ┐
                          (router/handoff)   └─A2A─►  KrediKartiUzmani─MCP─► Banking.Domain
                                                                            (in-memory simülasyon)
Tarayıcı ──/api/collect──► TAHSİLAT: CollectionAgent (slot-filling, LLM)  ──┘
                           → MAF Workflow + RequestPort (onay gate, HITL) → ICollectionService.Charge
```

- **MCP** = ajanların *veriye/araçlara* eriştiği yol. Uzmanlar süreç-içi MCP server'a (`/mcp`) MCP **client** olarak bağlanır.
- **A2A** = ajanların *başka ajanlara* eriştiği yol. Orkestratör uzmanlara A2A ile (`/a2a/hesap`, `/a2a/kart`) ulaşır.
- **Hibrit topoloji**: hepsi tek ASP.NET sürecinde host edilir; protokoller gerçek HTTP üzerinden loopback'te konuşur.

## Proje yapısı

| Proje | Sorumluluk |
|---|---|
| `src/Banking.Domain` | Modeller + in-memory veri deposu + servisler (Hesap, Kart, Tahsilat) |
| `src/Banking.Mcp` | Domain'i MCP araçlarına saran sınıflar (`[McpServerTool]`) |
| `src/Banking.Agents` | Uzman ajanlar, MCP client, A2A orkestratör, AgentRuntime, tahsilat workflow'u |
| `src/Banking.Host` | ASP.NET Core: API + MCP/A2A host + DI + basit web UI |
| `tools/ApiDump` | (dev) Kurulu MAF/MCP/A2A assembly'lerinin API yüzeyini reflection ile döker |
| `tools/WorkflowTest` | (dev) Tahsilat HITL onay gate'ini LLM olmadan deterministik test eder |

## Gereksinimler

- .NET 9 SDK
- Tool-calling destekli bir OpenAI modeli için **OpenAI API anahtarı** (`OPENAI_API_KEY`)

## Çalıştırma

API anahtarını ayarlamanın iki yolu var:

**A) User Secrets (önerilen — anahtar repo dışında, kullanıcı profilinde saklanır):**
```bash
dotnet user-secrets set "Agents:ApiKey" "sk-..." --project src/Banking.Host
dotnet run --project src/Banking.Host
```

**B) Ortam değişkeni:**
```powershell
$env:OPENAI_API_KEY = "sk-..."        # PowerShell   (bash: export OPENAI_API_KEY="sk-...")
dotnet run --project src/Banking.Host
```

Sonra tarayıcıda **http://localhost:5080** adresini aç.

> Anahtar önceliği: `Agents:ApiKey` (User Secrets / appsettings) > `OPENAI_API_KEY`. Anahtarı değiştirmek için
> aynı `user-secrets set` komutunu tekrar çalıştırman yeterli. Anahtarı `appsettings.json`'a yazma (paylaşılır/sızar).

Model `appsettings.json` → `Agents:Model` ile değiştirilebilir (varsayılan `gpt-4o-mini`).
Dinleme adresi `Agents:SelfBaseUrl` (varsayılan `http://localhost:5080`); loopback MCP/A2A çağrıları bu adresi kullanır.

## Endpoint'ler

| Endpoint | İş |
|---|---|
| `GET /` | Basit chat UI |
| `POST /api/chat` | `{ "message": "..." }` → orkestratör cevabı |
| `POST /api/collect/start` | Tahsilat akışını başlatır |
| `POST /api/collect/respond` | `{ "sessionId": "...", "answer": "..." }` → sıradaki soru / onay / makbuz |
| `GET  /api/diag` | Wiring tanılaması (anahtarsız bile çalışır) |
| `GET  /api/trace/stream` | Canlı katman izi (SSE) — UI'ın sağ paneli buna abone olur |
| `/mcp` | MCP server (Streamable HTTP) |
| `/a2a/hesap`, `/a2a/kart` | A2A (JSON-RPC) uzman endpoint'leri |

### Canlı katman izi (sağ panel)

UI iki panellidir: solda sohbet, sağda **canlı katman izi**. Her soruda çağrı zinciri gerçek zamanlı akar; her olay
katmanına göre renklendirilir:

```
SYSTEM    Kullanıcı sorusu alındı → Orkestratör
LLM       Orkestratör · model çağrısı
A2A       Kart Uzmanı  → /a2a/kart
LLM       Kart Uzmanı · model çağrısı
MCP       list_cards  → CardService
LLM       Kart Uzmanı · model çağrısı      (sentez)
LLM       Orkestratör · model çağrısı      (final sentez)
SYSTEM    Yanıt kullanıcıya hazır
```

İzleme, `IChatClient` (LLM çağrıları) ve `AIFunction` (A2A/MCP/Servis araçları) sarmalayıcılarıyla
([Tracing/](src/Banking.Agents/Tracing/)) toplanır ve global `TraceHub` üzerinden SSE ile yayınlanır — A2A
sınırının diğer tarafındaki uzman/MCP olayları da görünür.

## İki akış

**Q&A (orkestrasyon):** "Kredi kartı borcum ne kadar?"
`/api/chat → Orkestratör → (A2A) KrediKartiUzmani → (MCP) get_card_debt → Domain → cevap`

**Tahsilat (human-in-the-loop):** "Kart borcumu ödemek istiyorum" (UI'da 💳 düğmesi)
`CollectionAgent eksik bilgileri sorar (hangi kart / tutar / kaynak hesap) → bilgiler tamamlanınca
deterministik onay ekranı (MAF Workflow + RequestPort) → Onayla → Charge() → makbuz`.
LLM **parayı çekmez**; tahsilat yalnızca onay sonrası deterministik düğümde yapılır.

## Doğrulama durumu

Anahtarsız (offline) doğrulananlar:

- ✅ Solution temiz derlenir (.NET 9, 0 uyarı/0 hata).
- ✅ Sunucu ayağa kalkar; `GET /api/diag` → `mcpToolCount: 7`, `specialistTransport: "A2A"`, `orchestratorReady: true`.
- ✅ A2A endpoint'leri canlı (`GET /a2a/hesap` → 405; JSON-RPC POST → yapısal A2A yanıtı).
- ✅ Tahsilat HITL onay gate'i (`tools/WorkflowTest`): ONAY → makbuz + bakiye düşer; RED → değişiklik yok.

Gerçek `OPENAI_API_KEY` ile uçtan uca doğrulananlar (gpt-4o-mini):

- ✅ **Q&A / kart:** "borçlarım ne kadar" → orkestratör →A2A→ kart uzmanı →MCP→ "Visa ****4521: ₺12.450, Mastercard ****8830: ₺8.900" (seed ile birebir).
- ✅ **Q&A / hesap:** "bakiye + hareketler" → hesap uzmanı →MCP→ "₺48.750" + tüm hareketler (birebir doğru).
- ✅ **Tahsilat (HITL):** ajan kart/tutar/hesap sorularını kendisi sordu → deterministik onay ekranı → **ONAY** → makbuz `RCPT-...-5000`, borç 8.900→3.900, bakiye 48.750→43.750; **RED** → "hiçbir tutar tahsil edilmedi", borç değişmedi.
- ✅ **State tutarlılığı:** tahsilat sonrası Q&A güncellenmiş borcu/bakiyeyi okudu (ajanlar uydurmuyor, MCP araçlarını çağırıyor).

### Dev araçlarını çalıştırma (opsiyonel)

```bash
dotnet run --project tools/WorkflowTest      # tahsilat HITL onay gate testi (anahtar gerekmez)
dotnet run --project tools/ApiDump -- .       # MAF/MCP/A2A API yüzeyini maf_api.txt'e döker
```

## Notlar

- Tek süreçte birden çok **adlı** MCP server desteklenmediğinden, tek MCP server tüm araçları sunar; uzmanlar
  araçları **isimle filtreleyerek** alır (hesap araçları / kart araçları).
- A2A endpoint'leri startup'ta map edilirken süreç-içi MCP'ye loopback yapamayacağından (sunucu henüz dinlemiyor),
  uzmanlar `DeferredSpecialistAgent` ile **ilk çağrıda** (dinleme başladıktan sonra) tembel kurulur.
- Demo "bugün" tarihi sabittir: 30.06.2026 (`BankClock.Today`). Tek demo müşterisi: `CUST-001`.
