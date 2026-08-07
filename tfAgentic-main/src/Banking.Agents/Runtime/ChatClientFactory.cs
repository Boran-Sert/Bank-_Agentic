using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

namespace Banking.Agents.Runtime;

/// <summary>Yapılandırmadan OpenAI uyumlu <see cref="ChatClient"/> örneği üretir.</summary>
public static class ChatClientFactory
{
    /// <summary>Sağlayıcı, model ve endpoint ayarlarına göre sohbet istemcisini kurar.</summary>
    /// <exception cref="NotSupportedException">Yapılandırılan sağlayıcı desteklenmediğinde fırlatılır.</exception>
    public static ChatClient Create(AgentOptions options)
    {
        var isSupported = string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.Provider, "ollama", StringComparison.OrdinalIgnoreCase);
        if (!isSupported)
            throw new NotSupportedException($"Desteklenmeyen sağlayıcı: {options.Provider}");

        // Anahtar yoksa, ajan grafiğinin LLM çağrısı yapmadan kurulabilmesi (wiring testi)
        // için 'dummy' kullanılır. Gerçek istek (RunAsync) için geçerli bir anahtar gerekir.
        var key = string.IsNullOrWhiteSpace(options.ApiKey) ? "dummy" : options.ApiKey;
        var credential = new ApiKeyCredential(key);

        OpenAIClient client = string.IsNullOrWhiteSpace(options.Endpoint)
            ? new OpenAIClient(credential)
            : new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint!) });

        return client.GetChatClient(options.Model);
    }
}
