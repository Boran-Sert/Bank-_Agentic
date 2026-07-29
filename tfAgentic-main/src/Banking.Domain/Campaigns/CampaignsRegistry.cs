using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Banking.Domain.Campaigns;

/// <summary>
/// Sistemdeki kredi kartı ekstre tanımlarını (Banka_ekstra.json) yöneten ve sorgulama imkânı sunan registry sınıfı.
/// </summary>
public static class CampaignsRegistry
{
    private static readonly List<CampaignsDefinition> Items = new();
    private static readonly Dictionary<long, CampaignsDefinition> GuidIndex = new();

    static CampaignsRegistry()
    {
        LoadFromFileSystem();
    }

    /// <summary>Kayıtlı tüm ekstre işlem hareketlerini döner.</summary>
    public static IReadOnlyList<CampaignsDefinition> GetAll() => Items.AsReadOnly();

    /// <summary>Benzersiz işlem GUID (AllCycleGuid) değerine göre ekstre hareketini döner.</summary>
    public static CampaignsDefinition? GetByGuid(long allCycleGuid) =>
        GuidIndex.GetValueOrDefault(allCycleGuid);

    /// <summary>Kart numarasına göre filtrelenmiş ekstre hareketlerini döner.</summary>
    public static IEnumerable<CampaignsDefinition> GetByCardNo(string cardNo) =>
        Items.Where(x => x.CardNo.Equals(cardNo, StringComparison.OrdinalIgnoreCase));

    /// <summary>MCC sektör kategorisine (MarketAndShopping, Food, Clothing vb.) göre ekstre hareketlerini döner.</summary>
    public static IEnumerable<CampaignsDefinition> GetByMccGroup(string mccGroupType) =>
        Items.Where(x => x.StatementMccGroupType.Equals(mccGroupType, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sadece harcama (satış) niteliğindeki ekstre hareketlerini döner.</summary>
    public static IEnumerable<CampaignsDefinition> GetExpenses() =>
        Items.Where(x => x.IsExpense);

    /// <summary>Özel bir dosya yolundan ekstre verilerini yeniden yükler.</summary>
    public static void LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var jsonContent = File.ReadAllText(filePath);
            ParseAndRegister(jsonContent);
        }
        catch
        {
            // Yükleme hatasında mevcut durum korunur
        }
    }

    private static void LoadFromFileSystem()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Banka_ekstra.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Banka_ekstra.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "tfAgentic-main", "Banka_ekstra.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Banka_ekstra.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Banka_ekstra.json")
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var jsonContent = File.ReadAllText(path);
                    if (ParseAndRegister(jsonContent))
                    {
                        break;
                    }
                }
                catch
                {
                    // Sıradaki adrese geç
                }
            }
        }
    }

    private static bool ParseAndRegister(string jsonContent)
    {
        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        if (root.TryGetProperty("GetCreditCardCycleInfoForAllCardsResult", out var resultObj) &&
            resultObj.TryGetProperty("Result", out var innerRes) &&
            innerRes.TryGetProperty("MainCardTransaction", out var mainCards) &&
            mainCards.ValueKind == JsonValueKind.Array)
        {
            Items.Clear();
            GuidIndex.Clear();

            foreach (var cardItem in mainCards.EnumerateArray())
            {
                if (cardItem.TryGetProperty("CardTransactionList", out var txList) &&
                    txList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in txList.EnumerateArray())
                    {
                        var item = new CampaignsDefinition(
                            AllCycleGuid: t.GetProperty("AllCycleGuid").GetInt64(),
                            Amount: t.GetProperty("Amount").GetDecimal(),
                            CardNo: t.GetProperty("CardNo").GetString() ?? "",
                            CardType: t.GetProperty("CardType").GetString() ?? "",
                            CardLevel: t.GetProperty("Cardlevel").GetString() ?? "",
                            EmbossName: t.GetProperty("EmbossName").GetString() ?? "",
                            Explanation: t.GetProperty("Explanation").GetString() ?? "",
                            FinancialTransactionType: t.GetProperty("FinancialTransactionType").GetString() ?? "",
                            HasDigitalSlip: t.GetProperty("HasDigitalSlip").GetBoolean(),
                            IsInstallmentCampaignSuitable: t.GetProperty("IsInstallmentCampaignSuitable").GetBoolean(),
                            OverLimitAmount: t.GetProperty("OverLimitAmount").GetDecimal(),
                            PaymentTermRevenue: t.GetProperty("PaymentTermRevenue").GetDecimal(),
                            Point: t.GetProperty("Point").GetDecimal(),
                            PointFinancialTransactionType: t.GetProperty("PointFinancialTransactionType").GetString() ?? "",
                            PointRedemptionAmount: t.GetProperty("PointRedemptionAmount").GetDecimal(),
                            ProcessedDate: t.GetProperty("ProcessedDate").GetInt64(),
                            StatementMccGroupType: t.GetProperty("StatementMccGroupType").GetString() ?? "Other",
                            TrxDate: t.GetProperty("TrxDate").GetInt64(),
                            TxnAllOnusGuid: t.GetProperty("TxnAllOnusGuid").GetInt64(),
                            TxnDefiniton: t.GetProperty("TxnDefiniton").GetString() ?? ""
                        );

                        Items.Add(item);
                        GuidIndex[item.AllCycleGuid] = item;
                    }
                }
            }
            return Items.Count > 0;
        }
        return false;
    }
}
