namespace Banking.Agents.Workflows.Declarative;

/// <summary>
/// Bir MAF deklaratif ajan tanımının (YAML veya JSON) tam C# karşılığı. Sistemdeki tek iş akışı
/// kayıt biçimidir; bu tipe eklenmeyen bir YAML alanı çalışma zamanında okunmaz.
/// </summary>
public sealed class MicrosoftAgentDeclarativeDefinition
{
    /// <summary>Tanımın türü (<c>kind</c>). Yürürlükteki tek desteklenen değer <c>Prompt</c>'tur.</summary>
    public string Kind { get; set; } = "Prompt";

    /// <summary>Akışın sistem içindeki kanonik kimliği (<c>name</c>). Kayıt ve yönlendirme bu ada göre yapılır.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Kullanıcıya gösterilen ad (<c>displayName</c>). Boş bırakılırsa <see cref="Name"/> kullanılır.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Akışın ne yaptığının açıklaması (<c>description</c>). Semantik yönlendirici bu metni
    /// vektörleştirerek akışı eşleştirir; boş bırakılan akış otomatik olarak bulunamaz.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Ajana verilecek ek talimat metni (<c>instructions</c>). Ortak slot doldurma kuralları buna
    /// eklenir, yerine geçmez.
    /// </summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Onay ekranını üretecek UI ekran kodu (<c>mkCode</c>).</summary>
    public string? MkCode { get; set; }

    /// <summary>
    /// Model ayarları bloğu (<c>model</c>). <c>options</c> altındaki değerler akışın ajanına uygulanır;
    /// <c>id</c> okunmaz, çünkü süreçte tek bir sohbet istemcisi yapılandırılır.
    /// </summary>
    public DeclarativeModelConfig? Model { get; set; }

    /// <summary>Akışın toplayacağı slotların şeması (<c>outputSchema</c>).</summary>
    public DeclarativeSchemaConfig? OutputSchema { get; set; }

    /// <summary>
    /// Alternatif slot şeması (<c>inputSchema</c>). Yalnızca <see cref="OutputSchema"/> tanımlı
    /// değilse dikkate alınır.
    /// </summary>
    public DeclarativeSchemaConfig? InputSchema { get; set; }

    /// <summary>
    /// Onay sonrası banka çağrısının tanımı (<c>action</c>).
    /// <para>
    /// <b>Tanımlanmadığında akış bankayı hiç çağırmaz:</b> onay yalnızca kayda geçer ve makbuz
    /// üretilmez. Para hareketi bekleyen bir akışta bu bloğun eksikliği, kullanıcıya gerçekleşmemiş
    /// bir işlemin bildirilmesi anlamına gelir.
    /// </para>
    /// </summary>
    public DeclarativeActionConfig? Action { get; set; }
}

/// <summary>YAML'daki <c>action</c> bloğunun karşılığı: onay sonrası gidilecek banka ucu.</summary>
public sealed class DeclarativeActionConfig
{
    /// <summary>Banka çağrısının HTTP metodu (<c>action.method</c>). Para hareketleri <c>PUT</c> kullanır.</summary>
    public string Method { get; set; } = "PUT";

    /// <summary>Bankadaki hedef yol (<c>action.path</c>); örneğin <c>/api/card-payment</c>.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>YAML'daki <c>model</c> bloğunun karşılığı.</summary>
public sealed class DeclarativeModelConfig
{
    /// <summary>Model kimliği (<c>model.id</c>). Şu an okunmaz; süreçte tek bir sohbet istemcisi kullanılır.</summary>
    public string? Id { get; set; }

    /// <summary>Akışın ajanına uygulanacak model ayarları (<c>model.options</c>).</summary>
    public DeclarativeModelOptionsConfig? Options { get; set; }
}

/// <summary>
/// YAML'daki <c>model.options</c> bloğunun karşılığı.
/// <para>
/// <b>Alanların tamamı isteğe bağlıdır ve varsayılanları <c>null</c>'dır.</b> Yazılmayan alan akışa
/// dayatılmaz; yapılandırmadaki değer ya da modelin kendi varsayılanı geçerli olur. Buraya sabit bir
/// değer yazmak, o varsayılan zincirini ulaşılamaz kılar.
/// </para>
/// </summary>
public sealed class DeclarativeModelOptionsConfig
{
    /// <summary>Örnekleme sıcaklığı (<c>model.options.temperature</c>). Yüksek değer yanıt çeşitliliğini artırır.</summary>
    public float? Temperature { get; set; }

    /// <summary>Çekirdek örnekleme eşiği (<c>model.options.topP</c>).</summary>
    public float? TopP { get; set; }
}

/// <summary>YAML'daki <c>outputSchema</c> veya <c>inputSchema</c> bloğunun karşılığı: akışın slot şeması.</summary>
public sealed class DeclarativeSchemaConfig
{
    /// <summary>Şema tipi (<c>type</c>). Yalnızca <c>object</c> desteklenir; diğer değerler okunmaz.</summary>
    public string? Type { get; set; } = "object";

    /// <summary>Slot adı → slot tanımı eşlemesi (<c>properties</c>).</summary>
    public Dictionary<string, DeclarativePropertyConfig>? Properties { get; set; }
}

/// <summary>Şemadaki tek bir slotun tanımı.</summary>
public sealed class DeclarativePropertyConfig
{
    /// <summary>
    /// Slotun veri tipi; örneğin <c>string</c> veya <c>number</c>. <c>update_slot</c> gelen değeri bu
    /// tipe göre kabul veya reddeder.
    /// </summary>
    public string Type { get; set; } = "string";

    /// <summary>
    /// Slotun zorunlu olup olmadığı. Belirtilmediğinde zorunlu kabul edilir; <c>false</c> verilen
    /// slotlar eksik olsa bile akış onaya ilerleyebilir.
    /// </summary>
    public bool? Required { get; set; } = true;

    /// <summary>Slotun ajana ve kullanıcıya gösterilecek açıklaması.</summary>
    public string Description { get; set; } = string.Empty;
}
