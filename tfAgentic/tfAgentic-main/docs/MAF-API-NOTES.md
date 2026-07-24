# Doğrulanmış MAF / MCP / A2A .NET API Notları (Haziran 2026)

> Bu notlar, kurulu paketlerin assembly'leri üzerinde **reflection** ile (tools/ApiDump) ve web araştırmasıyla çapraz doğrulanmıştır. Hızlı evrilen bir framework olduğundan, eğitim verisindeki eski isimler (örn. `CreateAIAgent`, `AgentThread`) bu sürümde **derlenmez**.

## Paket sürümleri (kurulu)

| Paket | Sürüm |
|---|---|
| Microsoft.Agents.AI | 1.11.1 |
| Microsoft.Agents.AI.OpenAI | 1.11.1 |
| Microsoft.Agents.AI.Workflows | 1.11.1 |
| Microsoft.Agents.AI.A2A (consume) | 1.11.1-preview.260625.1 |
| Microsoft.Agents.AI.Hosting | 1.11.1-preview.260625.1 |
| Microsoft.Agents.AI.Hosting.A2A.AspNetCore | 1.11.1-preview.260625.1 |
| Microsoft.Extensions.AI.OpenAI | 10.7.0 |
| ModelContextProtocol (+ .Core) | 1.4.0 |
| ModelContextProtocol.AspNetCore | 1.4.0 |

## Önemli yeniden adlandırmalar (eski → güncel)

| Eski (kullanma) | Güncel |
|---|---|
| `chatClient.CreateAIAgent(...)` | **`chatClient.AsAIAgent(...)`** |
| `AgentThread` | **`AgentSession`** |
| `agent.GetNewThread()` | **`await agent.CreateSessionAsync()`** |
| `AgentRunResponse` / `...Update` | **`AgentResponse`** / **`AgentResponseUpdate`** |
| `McpClientFactory.CreateAsync` | **`McpClient.CreateAsync`** |
| `SseClientTransport` (HTTP) | **`HttpClientTransport`** + `HttpTransportMode` |
| `AgentWorkflowBuilder.BuildHandoff` | **`AgentWorkflowBuilder.CreateHandoffBuilderWith(...)`** |

## 1. OpenAI → AIAgent
```csharp
using OpenAI; using OpenAI.Chat; using Microsoft.Agents.AI;
ChatClient chat = new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini");
AIAgent agent = chat.AsAIAgent(instructions: "...", name: "X", description: "...", tools: toolList);
```

## 2. Araçlar
```csharp
using Microsoft.Extensions.AI;
AIFunction t = AIFunctionFactory.Create(MyMethod, name: "...", description: "...");
```

## 3. Çalıştırma + oturum
```csharp
AgentSession s = await agent.CreateSessionAsync();
AgentResponse r = await agent.RunAsync("merhaba", s);   // r.Text
```

## 4. Agent-as-tool
```csharp
AIFunction tool = specialist.AsAIFunction(new AIFunctionFactoryOptions { Name = "kart_uzmani", Description = "..." });
```

## 5. MCP client → ajan araçları
```csharp
using ModelContextProtocol.Client;
var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions {
    Endpoint = new Uri("http://localhost:PORT/mcp"), TransportMode = HttpTransportMode.AutoDetect, Name = "banking-mcp" }));
IList<McpClientTool> tools = await mcp.ListToolsAsync();   // McpClientTool : AIFunction
```

## 6. MCP server (ASP.NET)
```csharp
builder.Services.AddMcpServer().WithHttpTransport().WithTools<AccountsTools>().WithTools<CardsTools>();
app.MapMcp("/mcp");
```
> Not: Tek process'te birden çok **adlı** MCP server (farklı tool setleri ayrı path'lerde) 1.4.0'da desteklenmiyor (issue #1202). Çözüm: tek server + client tarafında isimle filtreleme.

## 7. A2A host
```csharp
using Microsoft.Agents.AI.Hosting;          // AddAIAgent
builder.Services.AddAIAgent("hesap", (sp, name) => sp.GetRequiredService<AgentRuntime>().GetSpecialist("hesap"));
app.MapA2AJsonRpc("hesap", "/a2a/hesap");    // Microsoft.Agents.AI.Hosting.A2A.AspNetCore
```

## 8. A2A consume (doğrudan endpoint, card-path tahmini yok)
```csharp
using A2A; using Microsoft.Agents.AI;
AIAgent remote = new A2AClient(new Uri($"{baseUrl}/a2a/kart")).AsAIAgent(name: "kart_uzmani", description: "...");
```

## 9. Workflow + Human-in-the-loop (onay gate)
```csharp
using Microsoft.Agents.AI.Workflows;
var port = RequestPort.Create<ConfirmationView, bool>("payment-approval");
var charge = new ChargeExecutor("charge", svc, slots);          // : Executor<bool>
Workflow wf = new WorkflowBuilder(port).AddEdge(port, charge).WithOutputFrom(charge).Build();

StreamingRun run = await InProcessExecution.RunStreamingAsync(wf, confirmationView);
await foreach (WorkflowEvent e in run.WatchStreamAsync()) {
    if (e is RequestInfoEvent req) { /* UI'a onay göster; cevap gelince: */ break; }
}
// onay geldiğinde (aynı run):
await run.SendResponseAsync(pendingRequest.CreateResponse(approved));   // ExternalRequest.CreateResponse<bool>
await foreach (WorkflowEvent e in run.WatchStreamAsync()) {
    if (e is WorkflowOutputEvent o && o.Is<PaymentReceipt>()) { var receipt = o.As<PaymentReceipt>(); break; }
}
```
Executor:
```csharp
sealed class ChargeExecutor(string id, ICollectionService svc, Slots s) : Executor<bool>(id) {
  public override async ValueTask HandleAsync(bool approved, IWorkflowContext ctx, CancellationToken ct = default) {
    object result = approved ? svc.Charge(s.CardId, s.Amount, s.SourceAccountId, s.Date) : new PaymentCancelled();
    await ctx.YieldOutputAsync(result, ct);
  }
}
```
Checkpoint (HTTP istekleri arası kalıcılık, opsiyonel): `CheckpointManager.CreateInMemory()` + `InProcessExecution.ResumeStreamingAsync(wf, checkpointInfo, cm)`. Bu projede tek süreç olduğundan `StreamingRun` bellekte tutulur.
