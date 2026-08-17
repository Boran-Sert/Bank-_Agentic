namespace Banking.Gateway.Models;

/// <summary>İstemcinin yanıtı nasıl işleyeceğini belirleyen yanıt tipleri.</summary>
public enum GatewayResponseType
{
    /// <summary>Düz metin yanıtı; sohbet balonunda gösterilir.</summary>
    TEXT,

    /// <summary>Ekran yönlendirmesi; istemci <c>mkCodeValue</c> ile ilgili ekranı açar.</summary>
    OPEN_SCREEN,

    /// <summary>Onay ekranı; kullanıcı işlemi onaylayana kadar hiçbir eylem çalışmaz.</summary>
    CONFIRM_PAYMENT
}
