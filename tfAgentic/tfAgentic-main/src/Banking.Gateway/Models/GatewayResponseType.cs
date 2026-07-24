namespace Banking.Gateway.Models;

// .NET → Java MW response tipleri.
// MW bu değere bakarak response'u nasıl ele alacağına karar verir
public enum GatewayResponseType
{
    /// Genel sohbet/bilgi yanıtı. MW text alanını olduğu gibi mobile iletir.
    /// Örnek: "Hesabınızın bakiyesi 48.750 
    TEXT,
    /// Slot-filling sorusu. Agent eksik bilgi soruyor.
    /// MW text alanını mobile iletir, sessionId'yi bir sonraki istekte geri gönderir
    QUESTION,
    /// Tamamlanmış ekran komutu. Tüm slotlar doldu, mkCodes + parametreler hazır.
    /// MW screenCommand alanını ScreenActionResponse'a çevirip mobile iletir
    SCREEN_COMMAND,
    /// İş hatası. MW errorCode ve errorMessage ile MiddlewareException fırlatır. 
    ERROR
}
