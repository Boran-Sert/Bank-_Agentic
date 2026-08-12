namespace Banking.BankApi.Contracts;

/*
 * ----------------------------------------------------------------------------------------------
 *  BANKANIN İSTEK SÖZLEŞMELERİ
 *  ---------------------------
 *  Bankadaki HER uç nokta parametrelerini YALNIZCA istek gövdesinden alır. Yol parametresi
 *  (/api/account/{id}) ve sorgu dizesi (?customerId=1) kullanılmaz; çağıran katman tek bir düz JSON
 *  nesnesi gönderir: {"customerId": "CUST-001"}.
 *
 *  Gövde anahtarları buradaki alan adlarının birebir aynısıdır. Minimal API'nin gövde bağlama
 *  çıkarımı karmaşık tipler için gövdeyi seçtiğinden [FromBody] gibi bir öznitelik yazılmaz;
 *  öznitelik eklemek, sözleşmenin tipin kendisinde değil imzada tutulması anlamına gelirdi.
 *
 *  Alanlar isteğe bağlı (nullable) tanımlanır ve zorunluluk denetimi uç noktanın kendisinde yapılır.
 *  Böylece eksik alan, model bağlamanın ürettiği anlaşılmaz 400 yerine bankanın kendi Türkçe
 *  gerekçesiyle döner; çağıran katman bu gerekçeyi olduğu gibi kullanıcıya taşıyabilir.
 * ----------------------------------------------------------------------------------------------
 */

/// <summary>Müşteri kırılımlı listeleme uçlarının gövdesi. Örn: <c>{"customerId": "CUST-001"}</c>.</summary>
public sealed record CustomerRequest(string? CustomerId);

/// <summary>Tek hesap sorgusunun gövdesi. Hesabın istenen müşteriye ait olduğu bankada denetlenir.</summary>
public sealed record AccountRequest(string? CustomerId, string? AccountId);

/// <summary>Hesap hareketi sorgusunun gövdesi. Tarih aralığı verilmezse tüm hareketler döner.</summary>
public sealed record AccountTransactionsRequest(
    string? CustomerId,
    string? AccountId,
    DateOnly? FromDate,
    DateOnly? ToDate);

/// <summary>Tek kart sorgusunun gövdesi. Kartın istenen müşteriye ait olduğu bankada denetlenir.</summary>
public sealed record CardRequest(string? CustomerId, string? CardId);

/// <summary>
/// Ekstre analizi uçlarının gövdesi. <paramref name="FilePath"/> verilmezse bankanın varsayılan
/// ekstre kaynağı okunur; <paramref name="TopCount"/> yalnızca işyeri sıralamasında kullanılır.
/// </summary>
public sealed record StatementRequest(string? FilePath, int? TopCount);

/// <summary>İki dönemin karşılaştırma isteğinin gövdesi. Tarihler ISO 8601 metin olarak taşınır.</summary>
public sealed record StatementCompareRequest(
    string? CurrentStart,
    string? CurrentEnd,
    string? PreviousStart,
    string? PreviousEnd,
    string? MccGroupType,
    string? FilePath);

/// <summary>Kampanya sorgusunun gövdesi. Kimlik verilmezse aktif kampanyaların tamamı döner.</summary>
public sealed record CampaignRequest(string? CampaignId);

/// <summary>Finansman kataloğu sorgusunun gövdesi. Tür verilmezse katalog bütünüyle döner.</summary>
public sealed record FinancingRequest(string? Type);

/// <summary>Kart borcu tahsilatının gövdesi. Geri alınamaz bir işlemi tetikler.</summary>
public sealed record CardPaymentRequest(
    string? CardId,
    decimal? Amount,
    string? SourceAccountId,
    DateOnly? Date);

/// <summary>Para transferinin gövdesi. Geri alınamaz bir işlemi tetikler.</summary>
public sealed record MoneyTransferRequest(
    string? SourceAccountId,
    string? TargetIban,
    decimal? Amount,
    string? Description);
