# XML Dokümantasyon Standardı

Bu dosya, koddaki `///` XML dokümantasyonunun nasıl yazılacağını tanımlar. Yeni bir sınıf, metot veya
sabit eklerken buradaki kurallar geçerlidir.

**Temel ilke:** dokümantasyon, projenin nasıl bu hâle geldiğini değil, **çalışan sistemin
sözleşmesini** anlatır. Değişim kaydı `CHANGELOG.md`'nin işidir.

---

## Kurallar

### S1 — Geçmiş zaman anlatısı yazılmaz

`eskiden`, `önceden`, `artık`, `…yazılıyordu`, `…silindi`, `…dağılmıştı`, `…gerektiriyordu` gibi
kalıplar kullanılmaz. Kod tabanına yeni gelen bir mühendis için, silinmiş bir tipten veya hiç
görmediği bir mimariden bahseden cümle bilgi taşımaz.

```csharp
// YANLIŞ
/// Eskiden aynı iş üç ayrı eksekütörde yazılıydı; her yeni akış dördüncü bir kopya gerektiriyordu.

// DOĞRU
/// Sistemdeki tek executor. Yeni bir akış eklemek bu sınıfta değişiklik gerektirmez.
```

### S2 — İlk cümle tek satır, tam ve kendi kendine yeter

Tipin veya üyenin *ne olduğunu* söyler ve nokta ile biter. IntelliSense tooltip'inde görünen tek satır
budur.

### S3 — Gerekçe, kuralın kendisi olarak yazılır

Korunması gereken bir değişmez varsa şimdiki zamanda ve normatif kiple ifade edilir. Yaşanmış bir
hata anlatılmaz; o hatanın **hangi kuralla** engellendiği yazılır.

```csharp
// YANLIŞ
/// Model teklif aracını çağırmadan tüm slotlar dolduğunda motor bu şablonu gerçek teklif
/// sanıp onay ekranını tamamı boş parametrelerle üretebiliyordu.

// DOĞRU
/// Şema ile teklif ayrı alanlarda tutulur. Beklenen slotların listesi hiçbir koşulda teklif
/// yerine geçmez; ikisinin tek alanda birleşmesi onay ekranının boş değerlerle üretilmesine
/// yol açar.
```

### S4 — Birinci çoğul şahıs ve konuşma dili yok

`kurtulmaya çalıştığımız`, `massage etmiyoruz`, `bugün kimse engellemiyor` gibi ifadeler yerine nesnel
üçüncü şahıs kullanılır.

### S5 — Retorik soru sorulmaz

Özellikle `bool` üyelerde yaygındır.

```csharp
// YANLIŞ   /// <summary>Akış hâlâ kullanıcı etkileşimi bekliyor mu?</summary>
// DOĞRU    /// <summary>Oturumda kullanıcı etkileşimi bekleyen bir akış bulunduğunu belirtir.</summary>
```

### S6 — Anekdot sayısı kullanılmaz

`otuz dört kez`, `on bir sınıf`, `yedi ayrı dosya`, `~30 sarmalayıcı` gibi ölçüler doğrulanamaz ve
zamanla yanlışa döner. Ölçü yerine kural yazılır.

### S7 — Vurgu disiplini

`<b>` yalnızca gerçek bir değişmezi işaretler ve paragraf başına en fazla bir kez kullanılır.
Her şeyin vurgulandığı bir blokta hiçbir şey vurgulanmamış olur. `<i>` kullanılmaz.

### S8 — Sabit etiket sırası

```
<summary>  → en fazla iki <para> (her biri tek konu)
<param>    → her parametre için, ya hepsi ya hiçbiri (yarısı CS1573 uyarısı üretir)
<returns>  → dönüş değeri açık değilse
<exception> → fırlatılan her istisna tipi için
```

### S9 — Atıflar canlı olmalı

Var olan tipe ve üyeye `<see cref="..."/>`, kod parçasına `<c>` kullanılır. Silinmiş bir tipe atıf
bırakılmaz — IDE'de tıklanamayan bağlantı üretir. Hedef tip, dosyanın `using` kapsamında değilse tam
nitelikli ad yazılır (`<see cref="Banking.Domain.Constants.TraceCategories"/>`). Atıfların canlılığı
derleyiciyle doğrulanabilir; aşağıdaki "Doğrulama" bölümüne bakınız.

### S10 — Terim sözlüğüne uyulur

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
| Tekrar koruması | **idempotency anahtarı** | `çift tahsilat koruması` (terim olarak; açıklama olarak kalabilir) |
| Dil modeli | **model** | `LLM` (metin içinde) |

**C# tip adları çevrilmez.** `WorkflowEngine`, `SessionContext`, `RequestPort` daima orijinal hâliyle
ve `<see cref>` veya `<c>` içinde yazılır.

---

## Doğrulama

Dokümantasyon uyarıları normal derlemede görünmez; XML üretimi açıkça istenmelidir. Çalışan
uygulamanın `bin` klasörünü kilitlememek için çıktı ayrı bir dizine yönlendirilir:

```powershell
dotnet build BankingAgents.sln -p:GenerateDocumentationFile=true -p:BaseOutputPath="$env:TEMP\docbuild\"
```

İlgili uyarılar:

| Kod | Anlamı | Ne yapılmalı |
|---|---|---|
| `CS1574` / `CS1584` | `cref` çözülemedi | S9 — atıfı düzelt veya `<c>` yap |
| `CS1573` | Bazı parametreler belgelenmiş, hepsi değil | S8 — eksik `<param>`'ı ekle |
| `CS1587` | XML yorumu bir dil öğesine bağlı değil | Bloğu ait olduğu bildirimin hemen üstüne taşı |
| `CS1591` | Public üyede dokümantasyon yok | Bilinçli olarak sıfırlanmadı; yeni kod için doldurulması beklenir |

Hedef: `CS1574`, `CS1584`, `CS1573` ve `CS1587` sayısı **sıfır**.

Bir dokümantasyon düzenlemesinin kodu değiştirmediğini kanıtlamak için:

```powershell
git diff -U0 -- '*.cs' | Select-String '^[+-]' |
  Select-String -NotMatch '^(\+\+\+|---)' |
  Select-String -NotMatch '^[+-]\s*///' |
  Select-String -NotMatch '^[+-]\s*$'
```

Çıktı boş olmalıdır. Desendeki `///` eşleşmesinin **satır başına** çapalandığına dikkat edin; sonuna
`$` eklemek yalnızca tamamen boş doküman satırlarını eler ve kontrolü sessizce işe yaramaz kılar.

---

## Dil

Dokümantasyon Türkçedir. Framework terimleri (slot, scope, executor, idempotency, endpoint) özgün
biçimleriyle ve S10 sözlüğüne uygun olarak kullanılır; zorlama çeviri yapılmaz.
