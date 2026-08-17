# XML Dokümantasyon Standardı

Bu dosya, koddaki `///` XML dokümantasyonunun ve satır içi `//` yorumlarının nasıl yazılacağını
tanımlar. Yeni bir sınıf, metot veya sabit eklerken buradaki kurallar geçerlidir.

**Temel ilke:** dokümantasyon, kodu ilk kez okuyan bir yazılımcının **çalışan sistemi anlaması** için
vardır. Sözleşmeyi anlatır: ne yapar, ne bekler, ne döner, neyi bozmamalı. Tasarım tartışması,
reddedilen alternatifler ve değişim kaydı koda değil, K9'daki dosyalara aittir.

---

## Kurallar

### K1 — Satır bütçesi

| Ne | Bütçe |
|---|---|
| Metot, property, alan, sabit | en fazla **2 satır** |
| Tip (class, record, interface, enum) | en fazla **6 satır** |

`<para>` **hiç kullanılmaz**; bütçe zaten paragrafı imkânsız kılar. Bütçeye sığmayan içerik
sadeleştirilir veya K9'a göre yerini bulur.

### K2 — İlk cümle sözleşmedir

Tipin veya üyenin *ne olduğunu / ne yaptığını* söyler, şimdiki zamandadır ve nokta ile biter.
IntelliSense tooltip'inde görünen tek satır budur.

```csharp
// DOĞRU
/// <summary>Adı verilen iş akışını aktif oturumda başlatır ve ilk soruyu döner.</summary>
```

### K3 — İkinci cümle yalnızca kısıt varsa yazılır

Çağıranı bağlayan ve **koddan okunamayan** bir kısıt varsa ikinci cümle eklenir. Yoksa yazılmaz.

```csharp
/// <summary>
/// Adı verilen iş akışını aktif oturumda başlatır ve ilk soruyu döner. Oturumda zaten bir akış
/// varsa yeni akış başlatılmaz; yarım kalmış bir işlemin düşürülmesi engellenir.
/// </summary>
```

### K4 — Karşı-olgusal anlatı yok

Kodun ne yaptığı yazılır; başka türlü yazılsaydı ne olacağı yazılmaz. Yasak kalıplar:

`-saydı/-seydi … -irdi/-ardı/-erdi` · `aksi hâlde` · `sessizce` · `önceden` · `eskiden` · `artık` ·
`daha kötüdür` · `görünmez olurdu` · `kaybolurdu` · `…yazılıyordu` · `…dağılmıştı`

Korunması gereken bir değişmez varsa **emir kipiyle tek cümle** yazılır.

```csharp
// YANLIŞ
/// Verilseydi, hangi oturuma yazılacağını modelin ürettiği bir metin belirlerdi.
// DOĞRU
/// Oturum kimliği hiçbir araca parametre olarak verilmez.

// YANLIŞ
/// Aksi hâlde tek bir açılış hatası, aramayı süreç ömrü boyunca sessizce sonuçsuz bırakırdı.
// DOĞRU
/// Yükleme başarısızsa indeks boş kalır ve sonraki çağrı yeniden dener.

// YANLIŞ
/// Alakasız bir kampanyayı önermek, hiç kampanya bulunamadığını söylemekten daha kötüdür.
// DOĞRU
/// Eşik yüksek tutulur; zayıf eşleşme sonuç sayılmaz.
```

### K5 — Süsleme yok

Kullanılmaz: `<b>`, `<i>`, retorik soru, birinci çoğul şahıs (`kurtulmaya çalıştığımız`), örnek
kullanıcı diyaloğu, doğrulanamaz sayı anekdotu (`otuz dört kez`, `~30 sarmalayıcı`).
`<c>` ve `<see cref>` serbesttir.

```csharp
// YANLIŞ   /// <summary>Akış hâlâ kullanıcı etkileşimi bekliyor mu?</summary>
// DOĞRU    /// <summary>Oturumda kullanıcı etkileşimi bekleyen bir akış bulunduğunu belirtir.</summary>
```

### K6 — Aşikâr üye belgelenmez

Adı sözleşmesini tam anlatan üye (`SessionId`, `IsCompleted`, düz DTO alanı) `<summary>` almaz.
Adı tekrarlayan doküman satırı okuyucuya bilgi taşımaz.

`<param>` ve `<returns>` yalnızca ad yetmediğinde yazılır. Bir metotta `<param>` yazılıyorsa
**tüm** parametreler için yazılır (yarısı `CS1573` üretir).

### K7 — Satır içi yorum bir adım etiketidir

2–4 kelimelik isim tamlaması, ait olduğu satır grubunun üstünde. Yalnızca metot bir aşamadan
diğerine geçtiğinde ve bu geçiş koddan göze çarpmadığında konur. **Metot başına en fazla üç.**
Kendini açıklayan satır (`var x = Foo();`) etiket almaz.

```csharp
// Aktif iş akışı kontrolü
if (sessionAccessor.Current.HasActiveWorkflow)
    return "Şu anda devam eden bir işlem var; yeni bir işlem başlatılamaz.";

// Akış üretimi
var engine = await workflowRegistry.CreateEngineAsync(workflowName);
var stepResult = await engine.AdvanceWorkflowStepAsync(initialMessage, cancellationToken);
```

Bölüm başlığı gereken uzun dosyalarda başlık **tek satırdır**: `// --- Akış durumu ---`.
Üç satırlık `// ====` çerçeveler ve numaralandırma (`// 1. AKIŞ DURUMU`) kullanılmaz.

### K8 — Bir kısıtın tek sahibi vardır

Aynı kural iki yerde yazılmaz. Sınıf başlığındaki cümle metotta, metottaki cümle satır içinde
tekrarlanmaz. Aynı kısıt iki dosyayı ilgilendiriyorsa yeri `CLAUDE.md`'dir; kodda tek cümlelik
karşılığı kalır.

### K9 — Gerekçenin yeri

| İçerik | Yeri |
|---|---|
| Ne yapar, ne bekler, ne döner | `///` |
| Kırılabilir değişmez | `///` — tek cümle, emir kipi |
| Neden bu tasarım, reddedilen alternatif | `CHANGELOG.md` |
| Katmanlar arası mimari kural | `CLAUDE.md` |
| Ölçüm, deney, takas | `docs/FINAL-PLAN.md` |

Bir gerekçe koddan çıkarılırken yük taşıyorsa **silinmez, taşınır**: önce hedef dosyada zaten var
mı diye bakılır, yoksa oraya yazılır.

### K10 — Terim sözlüğü

Aynı kavram her yerde tek bir adla anılır.

| Kavram | Kullanılır | Kullanılmaz |
|---|---|---|
| Akışın topladığı alan | **slot** | `parametre`, `alan` |
| `IDeterministicActionExecutor` | **executor** | `eksekütör` |
| Toplanan slot sözlüğü | **slot kümesi** | `StateBag` (yalnızca trace mesajlarında kalır) |
| Oturum kapsamı | **oturum scope'u** | `kapsam` |
| İş akışı | **iş akışı** (ilk geçişte) / **akış** (aynı blokta tekrar) | `workflow` |
| Onaya sunulan slot kümesi | **teklif** | `proposal`, `öneri` |
| Roller | **orkestratör**, **ajan**, **uzman** | `orchestrator`, `agent`, `specialist` |
| İnsan onayı kapısı | **onay kapısı (HITL)** — kısaltma ilk geçişte açılır | yalnızca `RequestPort` |
| Tekrar koruması | **idempotency anahtarı** | `çift tahsilat koruması` |
| Dil modeli | **model** | `LLM` (metin içinde) |

**C# tip adları çevrilmez.** `WorkflowEngine`, `SessionContext`, `RequestPort` daima orijinal hâliyle
ve `<see cref>` veya `<c>` içinde yazılır.

---

## Dil

Dokümantasyon Türkçedir. Framework terimleri (slot, scope, executor, idempotency, endpoint) özgün
biçimleriyle ve K10 sözlüğüne uygun kullanılır; zorlama çeviri yapılmaz.

---

## Doğrulama

### Atıflar ve etiket bütünlüğü

Dokümantasyon uyarıları normal derlemede görünmez; XML üretimi açıkça istenmelidir. Çalışan
uygulamanın `bin` klasörünü kilitlememek için çıktı ayrı bir dizine yönlendirilir:

```powershell
dotnet build BankingAgents.sln -p:GenerateDocumentationFile=true -p:BaseOutputPath="$env:TEMP\docbuild\"
```

| Kod | Anlamı | Ne yapılmalı |
|---|---|---|
| `CS1574` / `CS1584` | `cref` çözülemedi | Atıfı düzelt veya `<c>` yap |
| `CS1573` | Bazı parametreler belgelenmiş, hepsi değil | K6 — eksik `<param>`'ı ekle veya hepsini kaldır |
| `CS1587` | XML yorumu bir dil öğesine bağlı değil | Bloğu ait olduğu bildirimin hemen üstüne taşı |
| `CS1591` | Public üyede dokümantasyon yok | K6 gereği her üyede beklenmez; bilinçli olarak sıfırlanmadı |

Hedef: `CS1574`, `CS1584`, `CS1573` ve `CS1587` sayısı **sıfır**.

### Kalıp taraması

```powershell
# Sıfır olmalı
Select-String -Path src\*.cs -Recurse -Pattern '<para>|<b>|<i>'
Select-String -Path src\*.cs -Recurse -Pattern 'sessizce|aksi hâlde|daha kötüdür|önceden|eskiden|artık'
Select-String -Path src\*.cs -Recurse -Pattern '\w+(rdı|rdi|rdu|rdü)\b'
```

### Kodun değişmediği

Bir dokümantasyon düzenlemesinin kodu değiştirmediğini kanıtlamak için:

```powershell
git diff -U0 -- '*.cs' | Select-String '^[+-]' |
  Select-String -NotMatch '^(\+\+\+|---)' |
  Select-String -NotMatch '^[+-]\s*///' |
  Select-String -NotMatch '^[+-]\s*//[^/]' |
  Select-String -NotMatch '^[+-]\s*$'
```

Çıktı boş olmalıdır. Desendeki `///` eşleşmesinin **satır başına** çapalandığına dikkat edin; sonuna
`$` eklemek yalnızca tamamen boş doküman satırlarını eler ve kontrolü sessizce işe yaramaz kılar.
