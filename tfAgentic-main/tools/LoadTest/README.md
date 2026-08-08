# LoadTest — uçtan uca yük ve efor testi

Sistemi **yalnızca HTTP yüzeyinden** sürer: sohbet, iş akışı, onay kapısı (HITL), deklaratif kayıt,
canlı iz akışı ve tanılama uçları aynı anda çalışır. Projede hiçbir proje referansı yoktur; test,
çalışan sunucunun davranışını doğrular, süreç içindeki bir kopyasını değil.

## Çalıştırma

Üç süreç gerekir: banka servisi, ajan sunucusu, yük testi.

```powershell
# 1. terminal — banka
dotnet run --project src/Banking.BankApi

# 2. terminal — ajan sunucusu
dotnet run --project src/Banking.Host

# 3. terminal — yük testi (1000 sanal kullanıcı, tam eşzamanlı)
dotnet run --project tools/LoadTest -- --users 1000
```

Önce küçük bir koşumla doğrulamak için:

```powershell
dotnet run --project tools/LoadTest -- --users 20 --concurrency 20 --report yuk.md
```

Tüm anahtarlar: `dotnet run --project tools/LoadTest -- --help`

Çıkış kodu: `0` geçti · `1` sözleşme ihlali · `2` ön uçuş engeli · `3` argüman hatası.

## Ne koşar

Her sanal kullanıcı kendi oturum jetonuyla, kendi senaryosunu baştan sona koşar. Senaryolar
karıştırılarak dağıtılır; sunucu her an karışık trafik görür.

| Senaryo | Ağırlık | Yaptığı |
|---|---|---|
| `qa` | 40 | Tek soruluk sohbet: orkestratör → uzman → MCP → banka hattı. |
| `multiturn` | 10 | Aynı oturumda üç soru: oturum sürekliliği. |
| `pay` | 15 | Kart borcu ödeme: slot doldurma → onay ekranı → onay → makbuz. |
| `cancel` | 8 | Ödeme başlatıp vazgeçme: para hareketi olmaması gerekir. |
| `double` | 4 | Onay ekranında **aynı anda iki onay**: tek tahsilat beklenir. |
| `handoff` | 5 | Akış ortasında konu dışı soru: akışın bozulmaması gerekir. |
| `transfer` | 8 | IBAN'a transfer: onay kapısı + iki taraflı bakiye denetimi. |
| `nonbank` | 10 | Bankaya istek göndermeyen akış (limit artırımı): onay ekranı üretmemesi gerekir. |
| `declarative` | 0 | Koşum sırasında YAML akış kaydı (`--declarative <n>` ile açılır). |

Ağırlıklar `--mix qa=40,pay=60` gibi verilir; ifadede geçmeyen senaryolar sıfırlanır. Arka planda
ayrıca sağlık yoklamaları (`--probe-interval`) ve canlı iz aboneleri (`--sse`) koşum boyunca çalışır.

## Neyi doğrular

Bulgular iki başlıkta ayrılır. **Sözleşme ihlalleri** koşumu başarısız sayar; **kalite bulguları**
yalnızca raporlanır. Ayrım olmadan, yük altında modelin akışı tamamlayamaması sistem hatası gibi
okunur.

Sözleşme (koşumu başarısız sayar):

- **Para değişmezi** — bankadaki gerçek bakiye/borç değişimi, makbuzu görülen hareketlerin toplamıyla
  karşılaştırılır. Denetim bir aralıkla yapılır: alt sınır makbuzlu toplam, üst sınıra yanıtı
  kaybolan onay girişimleri eklenir. Aralığın altına düşmek "makbuz verildi, para hareket etmedi",
  üstüne çıkmak "onaylanmayan para hareketi" demektir.
- **Onay kapısı (HITL)** — onay verilmemiş hiçbir turda makbuz görülmemelidir; iptal edilen akış
  makbuz üretmemelidir.
- **Çift onay** — aynı onayın iki eşzamanlı isteği iki ayrı makbuz üretmemelidir.
- **Oturum izolasyonu** — her yanıt, isteği gönderen oturumun jetonunu döndürmelidir.
- **Onay ekranı içeriği** — onaya çıkan slot kümesi kullanıcının istediği kart/tutar/hesapla
  eşleşmelidir. Eşleşmiyorsa onay verilmez, akış iptal edilir.
- **Çıktı güvenlik bariyeri** — yanıt metinlerinde dahili araç adı, iç adres veya oturum jetonu
  bulunmamalıdır. Aranan araç adları `/api/tools` kataloğundan okunur.
- **Kapısız akış** — bankaya istek göndermeyen akış onay ekranı üretmemelidir.
- **Sunucu hatası** — hiçbir istek 5xx dönmemelidir. Zaman aşımı ve bağlantı kopması yük altında
  beklenen sonuçtur; ayrı raporlanır, ihlal sayılmaz.

Kalite (raporlanır): akışların verilen tur sayısında onaya ulaşma oranı, tutarın veya kart/hesap
kimliğinin değiştirilmesi, gecikme yüzdelikleri, hata dağılımı.

## Bilinmesi gerekenler

- **Banka verisi bellektedir ve koşumlar arasında sıfırlanmaz.** Planlanan toplam ödeme kart borcunu
  veya hesap bakiyesini aşarsa ön uçuş denetimi koşumu başlatmaz. Sıfırlamak için
  `Banking.BankApi`'yi yeniden başlatın; `--force` ile denetim atlanabilir.
- **Tutarlar yuvarlak değerlerden döngüyle atanır** (`--amounts`). Aynı (kart, tutar, hesap) üçlüsü
  farklı kullanıcılarda tekrarlanabildiği için oturumlar arası sızıntının kime ait olduğu her zaman
  tespit edilemez; sızıntının kendisi oturum jetonu denetimi ve para değişmeziyle yakalanır.
  Transfer senaryosu açıklama slotuna kullanıcıya özgü bir etiket yazar.
- **`--declarative` diske yazar.** Kayıt ucu tanımı `src/Banking.Host/declarative-agents/` altına
  kaydeder ve dosya sunucu yeniden başladığında geri yüklenir. Adlar sabit bir kümeden alınır
  (`yuk-testi-00`…), böylece tekrarlanan koşumlar dosya biriktirmez; koşum sonrası silinmeleri
  gerekir. Varsayılanı sıfırdır.
- **Zaman aşımı beklenmelidir.** 1000 eşzamanlı istek, tek bir model sunucusunun önünde kuyruğa
  girer. Ölçülen şey sistemin bu kuyruk altında sözleşmesini koruyup koruduğudur; `--timeout` ve
  `--max-turns` buna göre ayarlanır.
- `--users` ile istenen sayıya `--declarative <n>` kadar kullanıcı eklenir.
