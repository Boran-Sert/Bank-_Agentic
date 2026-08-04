using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

namespace Banking.Agents.Runtime;

/// <summary>OpenAI <see cref="ChatClient"/> örneğini yapılandırmadan üretir.</summary>
public static class ChatClientFactory
{
    public static ChatClient Create(AgentOptions options)
    {
        if (!string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase))
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
