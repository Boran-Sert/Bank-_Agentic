using System.Text.Json.Nodes;

namespace LoadTest;

/// <summary>
/// Bankanın belirli bir andaki hesap bakiyeleri ve kart borçları. Para değişmezleri koşum öncesi ve
/// sonrası iki anlık görüntünün farkından hesaplanır.
/// </summary>
/// <param name="Balances">Hesap kimliği → bakiye.</param>
/// <param name="Debts">Kart kimliği → güncel borç.</param>
/// <param name="Ibans">Hesap kimliği → IBAN.</param>
public sealed record BankSnapshot(
    IReadOnlyDictionary<string, decimal> Balances,
    IReadOnlyDictionary<string, decimal> Debts,
    IReadOnlyDictionary<string, string> Ibans)
{
    /// <summary>Verilen hesabın bakiyesini döner; hesap görülmediyse <c>null</c>.</summary>
    public decimal? Balance(string accountId) => Balances.TryGetValue(accountId, out var value) ? value : null;

    /// <summary>Verilen kartın borcunu döner; kart görülmediyse <c>null</c>.</summary>
    public decimal? Debt(string cardId) => Debts.TryGetValue(cardId, out var value) ? value : null;

    /// <summary>Verilen IBAN'a sahip hesabın kimliğini döner; eşleşme yoksa <c>null</c>.</summary>
    public string? FindAccountByIban(string iban)
    {
        var normalized = Normalize(iban);
        return Ibans.FirstOrDefault(entry => Normalize(entry.Value) == normalized).Key;
    }

    /// <summary>IBAN'ları boşluk ve harf büyüklüğünden bağımsız karşılaştırmak için normalleştirir.</summary>
    public static string Normalize(string iban) =>
        new(iban.Where(character => !char.IsWhiteSpace(character)).Select(char.ToUpperInvariant).ToArray());
}

/// <summary>
/// Banka durumunu okuyan ve koşum planının banka verisiyle tutarlı olup olmadığını denetleyen
/// yardımcı.
/// </summary>
public sealed class BankProbe(ApiClient bank, LoadTestOptions options)
{
    /// <summary>
    /// Hesap ve kart durumunu okur.
    /// <para>
    /// Okuma bankanın kendi uçlarından yapılır; ajan katmanı devrede değildir. Bu sayede para
    /// değişmezi, testin sürdüğü hattan bağımsız bir kaynağa dayanır.
    /// </para>
    /// </summary>
    /// <param name="label">Ölçüm etiketi; koşum öncesi ve sonrası okumaları ayırır.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <returns>Anlık görüntü; okuma başarısızsa <c>null</c>.</returns>
    public async Task<BankSnapshot?> ReadAsync(string label, CancellationToken cancellationToken)
    {
        var accounts = await bank.SendJsonAsync(
            HttpMethod.Post, "/api/accounts", new { customerId = options.CustomerId }, $"bank:{label}:accounts", cancellationToken);

        var cards = await bank.SendJsonAsync(
            HttpMethod.Post, "/api/cards", new { customerId = options.CustomerId }, $"bank:{label}:cards", cancellationToken);

        if (!accounts.Ok || !cards.Ok) return null;

        var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var ibans = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var debts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in EnumerateArray(accounts.Value))
        {
            var id = ReadString(account, "id");
            if (id is null) continue;

            balances[id] = ReadDecimal(account, "balance") ?? 0m;
            ibans[id] = ReadString(account, "iban") ?? string.Empty;
        }

        foreach (var card in EnumerateArray(cards.Value))
        {
            var id = ReadString(card, "id");
            if (id is null) continue;

            debts[id] = ReadDecimal(card, "currentDebt") ?? 0m;
        }

        return new BankSnapshot(balances, debts, ibans);
    }

    /// <summary>
    /// Planın banka verisiyle çalıştırılabilir olduğunu denetler.
    /// <para>
    /// Banka durumu bellekte tutulur ve koşumlar arasında sıfırlanmaz. Planlanan toplam ödeme kart
    /// borcunu veya hesap bakiyesini aşıyorsa akışlar bankanın haklı reddine takılır ve para
    /// değişmezi ölçülemez hâle gelir; bu durum koşum başlamadan bildirilir.
    /// </para>
    /// </summary>
    /// <param name="snapshot">Koşum öncesi banka durumu.</param>
    /// <param name="plans">Sanal kullanıcı görevleri.</param>
    /// <returns>Engel listesi; boş liste planın çalıştırılabilir olduğunu belirtir.</returns>
    public List<string> Validate(BankSnapshot snapshot, IReadOnlyList<JourneyPlan> plans)
    {
        var blockers = new List<string>();

        var plannedPerCard = plans
            .Where(plan => plan.MovesMoney && plan.CardId is not null)
            .GroupBy(plan => plan.CardId!)
            .ToDictionary(group => group.Key, group => group.Sum(plan => plan.Amount ?? 0m));

        foreach (var (cardId, planned) in plannedPerCard)
        {
            var debt = snapshot.Debt(cardId);

            if (debt is null)
            {
                blockers.Add($"Kart '{cardId}' bankada bulunamadı.");
                continue;
            }

            if (planned > debt)
                blockers.Add($"Kart '{cardId}': planlanan ödeme {planned:N2} ₺, güncel borç {debt:N2} ₺ — borç yetmiyor.");
        }

        var plannedFromAccount = plans.Where(plan => plan.MovesMoney).Sum(plan => plan.Amount ?? 0m);
        var balance = snapshot.Balance(options.SourceAccountId);

        if (balance is null)
            blockers.Add($"Hesap '{options.SourceAccountId}' bankada bulunamadı.");
        else if (plannedFromAccount > balance)
            blockers.Add($"Hesap '{options.SourceAccountId}': planlanan çıkış {plannedFromAccount:N2} ₺, bakiye {balance:N2} ₺ — bakiye yetmiyor.");

        return blockers;
    }

    private static IEnumerable<JsonNode?> EnumerateArray(JsonNode? node) =>
        node as JsonArray ?? [];

    private static string? ReadString(JsonNode? node, string name) =>
        node?[name]?.ToString();

    private static decimal? ReadDecimal(JsonNode? node, string name)
    {
        var value = node?[name];
        if (value is null) return null;

        try
        {
            return value.GetValue<decimal>();
        }
        catch (Exception)
        {
            return decimal.TryParse(
                value.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ? parsed : null;
        }
    }
}
