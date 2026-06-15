# AGUIWithMcpTools

A three-project sample showing how to host a real out-of-process MCP server, register its tools with an `IChatClient`, and expose that client through AG-UI.

| Project | Role |
|---|---|
| [`AGUIWithMcpToolsMcpServer/`](./AGUIWithMcpToolsMcpServer/) | Standalone console app that runs an MCP server over **stdio**. Hosts the `[McpServerTool]` methods (`kb_search`, `kb_list`) from [`KnowledgeBaseTools.cs`](./AGUIWithMcpToolsMcpServer/KnowledgeBaseTools.cs). Logging is routed to **stderr** because stdout is the JSON-RPC channel. |
| [`AGUIWithMcpToolsServer/`](./AGUIWithMcpToolsServer/) | ASP.NET Core AG-UI host. **Spawns the MCP server as a child process** via `StdioClientTransport`, exposes its tools via `McpClient.ListToolsAsync()`, and wires them into `ChatOptions.Tools` on an Azure OpenAI–backed `IChatClient`. |
| [`AGUIWithMcpToolsConsoleClient/`](./AGUIWithMcpToolsConsoleClient/) | Console app that POSTs three scenarios at the running server via `AGUIChatClient`, exercising both MCP tools. |

## What it shows

The AG-UI server side wires three pieces together:

| Layer | Implementation |
|---|---|
| **MCP server (separate process)** | `Host.CreateApplicationBuilder` + `.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`. Discovers `[McpServerToolType]` classes automatically. |
| **MCP client** | `new StdioClientTransport(new StdioClientTransportOptions { Command = <path-to-mcp-exe> })` → `McpClient.CreateAsync(transport)` → `client.ListToolsAsync()`. Each `McpClientTool` extends `AIFunction`, so it slots straight into `ChatOptions.Tools`. |
| **AG-UI endpoint** | Reads `RunAgentInput`, calls the registered `IChatClient` (Azure OpenAI) with the MCP tools attached, converts the streaming response to AG-UI events. |

The AG-UI host owns the child MCP process and disposes the `McpClient` on `IHostApplicationLifetime.ApplicationStopping`, which terminates the child cleanly.

## Why a separate process

In a real deployment MCP servers run independently (often distributed as binaries from third parties) and are launched by the host over stdio or streamed HTTP. Modelling the sample this way keeps it aligned with how MCP is used in production.

The AG-UI project declares a build-only project reference so MSBuild builds the MCP server first without linking its assemblies:

```xml
<ProjectReference Include="..\AGUIWithMcpToolsMcpServer\AGUIWithMcpToolsMcpServer.csproj"
                  ReferenceOutputAssembly="false" />
```

## Prerequisites

This sample uses real Azure OpenAI — there is no fake client fallback.

1. An Azure OpenAI resource with a `gpt-5-mini` deployment (the sample defaults are pre-wired to the team's `ag-ui-agent-framework` resource).
2. `az login` so `DefaultAzureCredential` can resolve credentials.

`launchSettings.json` pre-populates:

| Variable | Default |
|---|---|
| `AZURE_OPENAI_ENDPOINT` | `https://ag-ui-agent-framework.cognitiveservices.azure.com/` |
| `AZURE_OPENAI_DEPLOYMENT_NAME` | `gpt-5-mini` |

Override either to target a different resource or model.

## Running

Start the AG-UI server (defaults to `http://localhost:5018`):

```sh
cd sdks/dotnet/samples/AGUIWithMcpTools/AGUIWithMcpToolsServer
dotnet run
```

The server spawns the MCP server child process automatically.

In another terminal, run the console client:

```sh
cd sdks/dotnet/samples/AGUIWithMcpTools/AGUIWithMcpToolsConsoleClient
dotnet run
```

Expected output: three `SCENARIO` blocks. The first two show the LLM emitting a `kb_search` tool call, receiving the result, and then summarising it. The third shows a `kb_list` call enumerating the available article IDs (`agui, mcp, copilot`).

Pass a custom server URL as the first argument: `dotnet run -- http://localhost:8080`.
