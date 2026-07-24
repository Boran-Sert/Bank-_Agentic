# Analiz & Tasarım Dokümanı — MAF Tabanlı Bankacılık Asistanı

> Microsoft Agent Framework (MAF) · .NET · OpenAI · ASP.NET Web API + basit UI · Hibrit (tek süreç) MCP/A2A

## 1. Amaç

Tam agentic bir bankacılık asistanı:

- **Orchestrator agent** kullanıcı sorularını karşılar, doğru uzman ajana yönlendirir, cevabı toparlar.
- **Uzman ajanlar** (skills) ilgili bankacılık servislerini MCP araçları üzerinden çağırır.
- **A2A** ile orchestrator ↔ uzman ajan iletişimi.
- **Human-in-the-loop tahsilat akışı**: ajan eksik bilgileri kendisi sorar, sonunda deterministik bir onay ekranı çıkar, kullanıcı onaylarsa tahsilat servisi çağrılır.

## 2. Mimari İlke: İki Protokol, İki Rol

- **MCP = ajanların veriye/araçlara eriştiği yol.** Uzman ajanlar bankacılık servislerine MCP client olarak bağlanır.
- **A2A = ajanların başka ajanlara eriştiği yol.** Orchestrator, uzman ajanlara A2A client olarak bağlanır.

"Hibrit" topoloji: hepsi tek ASP.NET süreci içinde host edilir; protokoller gerçek HTTP üzerinden loopback'te konuşur (sahte değil, sadece tek process'te toplanmış). İleride uzman ajanları ayrı sürece taşımak için yalnızca URL değişir.

```
                       ┌─────────────────────────────────────────────┐
   Tarayıcı            │            Banking.Host (ASP.NET)            │
  (basit UI)           │                                             │
      │  /api/chat     │   ┌────────────────────────────────────┐    │
      ├───────────────►│   │   ORCHESTRATOR AGENT (router)       │    │
      │                │   │   - handoff ile yönlendirme         │    │
      │                │   └───────┬───────────────┬────────────┘    │
      │                │      A2A  │           A2A │                  │
      │                │  ┌────────▼─────┐   ┌─────▼──────────┐       │
      │                │  │ HesapAgent   │   │ KrediKartiAgent│       │
      │                │  └──────┬───────┘   └───────┬────────┘       │
      │                │     MCP │               MCP │               │
      │                │  ┌──────▼───────┐   ┌───────▼────────┐       │
      │                │  │ Accounts MCP │   │  Cards MCP     │       │
      │                │  └──────┬───────┘   └───────┬────────┘       │
      │                │         └───────┬───────────┘               │
      │  /api/collect  │          ┌──────▼──────────────┐            │
      ├───────────────►│          │  Banking.Domain      │            │
      │  (HITL akışı)  │          │  (in-memory simülas.) │            │
      │◄───────────────│   ┌──────▼──────┐                          │
      │  onay ekranı   │   │ TAHSİLAT     │◄── human-in-the-loop      │
      │                │   │ WORKFLOW(MAF)│     (onay gate)           │
      └────────────────┴───┴──────────────┴──────────────────────────┘
```

## 3. Solution Yapısı

```
BankingAgents.sln
├── src/
│   ├── Banking.Domain/        # Saf C#: modeller + in-memory veri + servisler
│   ├── Banking.Mcp/           # Domain'i MCP araçlarına saran katman
│   ├── Banking.Agents/        # MAF ajanları + workflow + A2A bağlama
│   └── Banking.Host/          # Composition root (ASP.NET Core + UI)
└── ANALIZ.md
```

## 4. Bankacılık Domain Simülasyonu

In-memory veri deposu + servisler:

| Servis | Örnek metotlar |
|---|---|
| `IAccountService` | `GetAccounts(customerId)`, `GetBalance(accountId)`, `GetTransactions(accountId, from, to)` |
| `ICardService` | `GetCards(customerId)`, `GetCardDebt(cardId)`, `GetStatement(cardId)`, `GetAvailableLimit(cardId)` |
| `ICollectionService` | `GetCollectionCase(cardId)`, `Charge(cardId, amount, sourceAccountId, date)` → makbuz |

`Charge` geri alınamaz kabul edilir; bu yüzden LLM'in doğrudan çağırmasına izin verilmez (bkz. §7).

## 5. MCP Katmanı (araç erişimi)

- **Accounts MCP server** → `/mcp/accounts` → `get_account_balance`, `list_accounts`, `get_transactions`
- **Cards MCP server** → `/mcp/cards` → `list_cards`, `get_card_debt`, `get_statement`, `get_available_limit`

Uzman ajanlar bu endpoint'lere MCP client olarak bağlanır; gelen araçlar doğrudan MAF ajanına `AITool` olarak verilir.

## 6. Ajanlar / Skiller

| Ajan | Rol | Araçları | Protokol |
|---|---|---|---|
| **OrchestratorAgent** | Soruyu sınıflandırır, doğru uzmana handoff eder, cevabı toparlar | uzman ajanlar (A2A) | A2A client |
| **HesapAgent** | Hesap/bakiye/işlem soruları | Accounts MCP araçları | MCP client + A2A server |
| **KrediKartiAgent** | Kart borcu/limit/ekstre soruları | Cards MCP araçları | MCP client + A2A server |

Uzman ajanlar `/a2a/account-agent` ve `/a2a/card-agent` adreslerinde A2A server olarak yayınlanır.

## 7. Tahsilat Akışı — Human-in-the-Loop Workflow

MAF Workflow + checkpoint. İki katmanlı güvenlik:

**Aşama A — LLM destekli slot-filling:** `CollectionAgent` eksik bilgiyi kendisi sorar:
1. Hangi kart / borç
2. Ödenecek tutar (tamamı / kısmi)
3. Kaynak hesap
4. Ödeme tarihi

**Aşama B — Deterministik onay gate:** Slotlar dolunca workflow LLM'i devre dışı bırakır, net bir onay özeti üretir ve UI'a onay ekranı gönderir:

```
┌──────────── ÖDEME ONAYI ────────────┐
│ Kart:        **** 4521              │
│ Borç:        12.450,00 ₺            │
│ Ödenecek:     5.000,00 ₺ (kısmi)    │
│ Kaynak hesap: TR.. ...3344          │
│ Tarih:       30.06.2026             │
│   [ ✓ Onayla ]      [ ✗ İptal ]     │
└──────────────────────────────────────┘
```

- **Onayla** → workflow deterministik olarak `Charge(...)` çağırır → makbuz.
- **İptal** → hiçbir servis çağrılmaz.

Kritik karar: `Charge`'ı LLM çağırmaz; yalnızca onay-sonrası deterministik düğüm çağırır.

## 8. Web API + UI

| Endpoint | İş |
|---|---|
| `GET /` | Basit chat UI |
| `POST /api/chat` | Soru → Orchestrator → cevap |
| `POST /api/collect/start` | Tahsilat workflow'unu başlatır |
| `POST /api/collect/respond` | Kullanıcı cevabı/onayı → sıradaki adım/onay/makbuz |
| `/mcp/accounts`, `/mcp/cards` | MCP server endpoint'leri |
| `/a2a/account-agent`, `/a2a/card-agent` | A2A server endpoint'leri |

## 9. Teknoloji / NuGet

- **MAF:** `Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.A2A`
- **LLM:** `OpenAI` + `Microsoft.Extensions.AI.OpenAI`
- **MCP:** `ModelContextProtocol` (+ AspNetCore host, Client)
- **Host:** ASP.NET Core (.NET 9)
- **Config:** `OPENAI_API_KEY` ortam değişkeni + `appsettings.json` (model adı)

## 10. Örnek Uçtan Uca İzler

**Q&A:** "Kredi kartı borcum ne kadar?"
`UI → /api/chat → Orchestrator (handoff: kart) → A2A → KrediKartiAgent → MCP get_card_debt → Cards MCP → CardService → 12.450 ₺ → geri`

**Tahsilat:** "Kart borcumu ödemek istiyorum"
`/api/collect/start → CollectionAgent eksikleri sorar → slotlar dolunca onay ekranı (deterministik) → Onayla → Charge() → makbuz`
