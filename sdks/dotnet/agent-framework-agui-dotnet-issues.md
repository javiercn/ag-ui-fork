# microsoft/agent-framework — Open Issues labeled `.NET` + AG-UI

Open issues in [microsoft/agent-framework](https://github.com/microsoft/agent-framework) carrying **both** the `.NET` label and the AG-UI label.

> Note: there is no literal `ag-ui` label in the repo. The AG-UI label is **`ui integration`** ("Agent User Interface Integration - AG-UI, ChatKit, etc"), so this set also includes DevUI / ChatKit items that share the `.NET` + `ui integration` pairing.

Collected: 2026-06-17 · Total open: **53**

| # | Title | Created | Updated | Author |
| --- | --- | --- | --- | --- |
| [#6519](https://github.com/microsoft/agent-framework/issues/6519) | .NET: [Feature]: Make AG-UI hosting (MapAGUI) transport-extensible – currently SSE-only despite the AGUI transport-agnostic spec | 2026-06-15 | 2026-06-17 | adamRoyd |
| [#6511](https://github.com/microsoft/agent-framework/issues/6511) | .NET: [Bug]: Tool Call Error in AGUI Mapping (WorkflowAsAgent with Session) 📌 Description | 2026-06-14 | 2026-06-17 | Mohanr1122 |
| [#6479](https://github.com/microsoft/agent-framework/issues/6479) | .NET: [Bug]: Frontend tools break persisted chat history | 2026-06-11 | 2026-06-17 | kpobb1989 |
| [#6006](https://github.com/microsoft/agent-framework/issues/6006) | .NET: [Bug]: DEVUI Read unrecognized type discriminator id 'function_approval_response'. | 2026-05-21 | 2026-06-17 | wyarcdev |
| [#5891](https://github.com/microsoft/agent-framework/issues/5891) | .NET: [Feature]: DevUI: Add edit, regenerate, and rerun support for conversation messages | 2026-05-15 | 2026-06-17 | GregorBiswanger |
| [#5806](https://github.com/microsoft/agent-framework/issues/5806) | .NET: [Bug]: DevUI integration in Aspire does not have OpenTelemetry visibility | 2026-05-13 | 2026-06-17 | hansmbakker |
| [#5781](https://github.com/microsoft/agent-framework/issues/5781) | .NET: [Bug]: Following the Aspire DevUI Readme results in `Error: CLIENT_ERROR: Agent 'agentservice' not found.` | 2026-05-12 | 2026-06-17 | hansmbakker |
| [#5779](https://github.com/microsoft/agent-framework/issues/5779) | .NET: [Bug]: Following the Aspire DevUI Readme results in 404 Not Found | 2026-05-12 | 2026-06-17 | hansmbakker |
| [#5614](https://github.com/microsoft/agent-framework/issues/5614) | .NET: [Feature]: Convert Microsoft.Agents.AI.Hosting.AGUI.AspNetCore to .NET Standard | 2026-05-03 | 2026-06-17 | kpobb1989 |
| [#5607](https://github.com/microsoft/agent-framework/issues/5607) | .NET: [Docs] Request for .NET DevUI Documentation & Integration Guide | 2026-05-01 | 2026-06-17 | Dudam-Neeraj-Dattu |
| [#5587](https://github.com/microsoft/agent-framework/issues/5587) | .NET: [Bug]: AGUI client crashes on non-JSON TOOL_CALL_RESULT content from handoff with JsonException ('T' is an invalid start of a value) | 2026-04-30 | 2026-06-17 | statto1974 |
| [#5567](https://github.com/microsoft/agent-framework/issues/5567) | .NET: [Bug]: BaseEventJsonConverter.Read is missing deserialization mapping for StateDeltaEvent | 2026-04-29 | 2026-06-17 | largeprob |
| [#5209](https://github.com/microsoft/agent-framework/issues/5209) | .NET: Make AG-UI conversion API public for multi-agent orchestrations | 2026-04-10 | 2026-06-17 | darthmolen |
| [#5203](https://github.com/microsoft/agent-framework/issues/5203) | .NET: [Feature]: Add a session when initiating an agent conversation in DevUI. | 2026-04-10 | 2026-06-17 | strikene |
| [#4920](https://github.com/microsoft/agent-framework/issues/4920) | .NET: AG-UI MapAGUI() doesn't use session store or map message AuthorName | 2026-03-26 | 2026-06-17 | graemefoster |
| [#4902](https://github.com/microsoft/agent-framework/issues/4902) | DOTNET - AG-UI: events should make it clear which agent is executing for multi-agent workflows | 2026-03-25 | 2026-06-17 | graemefoster |
| [#4869](https://github.com/microsoft/agent-framework/issues/4869) | .NET: [Bug]: AGUIChatClient sets ConversationId in response despite being stateless | 2026-03-23 | 2026-06-17 | ArturDorochowicz |
| [#4825](https://github.com/microsoft/agent-framework/issues/4825) | .NET [Feature]: GitHubCopilotAgent should translate Copilot SDK permission.requested events to AG-UI human-in-the-loop events | 2026-03-21 | 2026-06-17 | ShrayRastogi |
| [#4635](https://github.com/microsoft/agent-framework/issues/4635) | .NET: [Bug]: Not all DataContent properties are propagated from server to client via AG-UI | 2026-03-11 | 2026-06-17 | Dazfl |
| [#4342](https://github.com/microsoft/agent-framework/issues/4342) | .NET: [Bug]: When converting from AGUIToolMessage to ChatMessage, the MessageId is lost. | 2026-02-27 | 2026-06-17 | IharYakimush |
| [#4177](https://github.com/microsoft/agent-framework/issues/4177) | .NET: [Feature]: Automatic translation of StateBag mutations and streaming tool-argument deltas to AG-UI state events | 2026-02-23 | 2026-06-17 | adner |
| [#3975](https://github.com/microsoft/agent-framework/issues/3975) | Python: .NET: [Bug]: DevUI returning 401 code execution_error | 2026-02-16 | 2026-06-17 | NicoVermeir |
| [#3962](https://github.com/microsoft/agent-framework/issues/3962) | .NET: [Bug]: [AG-UI] MapAGUI reuses the same messageId for consecutive TOOL_CALL_RESULT SSE events | 2026-02-16 | 2026-06-17 | gerardogreco-psen |
| [#3823](https://github.com/microsoft/agent-framework/issues/3823) | .NET: [Bug]: [AG-UI] [Workflows] Session is always null in the middleware | 2026-02-10 | 2026-06-17 | ecc-parity-check |
| [#3790](https://github.com/microsoft/agent-framework/issues/3790) | .NET: [Bug]: AG-UI hosting drops FinishReason on RunFinishedEvent, breaking client-side tool execution | 2026-02-10 | 2026-06-17 | erikostling |
| [#3769](https://github.com/microsoft/agent-framework/issues/3769) | .NET: Python: .NET: [Feature]: Decouple AG-UI Protocol from Transport | 2026-02-09 | 2026-06-17 | castlenthesky |
| [#3752](https://github.com/microsoft/agent-framework/issues/3752) | .NET: [Bug]: [AG-UI] Usage and Annotations are not present | 2026-02-08 | 2026-06-17 | ecc-parity-check |
| [#3729](https://github.com/microsoft/agent-framework/issues/3729) | .NET: [Bug]:  AG-UI ASP.NET Core endpoint crashes on multimodal user messages (content array) | 2026-02-06 | 2026-06-17 | thoemmi |
| [#3723](https://github.com/microsoft/agent-framework/issues/3723) | .NET: [Bug]: OpenTelemetry is not emitted when Agent is triggered through AGUI | 2026-02-06 | 2026-06-17 | viul-sc |
| [#3684](https://github.com/microsoft/agent-framework/issues/3684) | .NET: UsageContent not returned in AG-UI stream | 2026-02-04 | 2026-06-17 | Dazfl |
| [#3520](https://github.com/microsoft/agent-framework/issues/3520) | .NET: [Feature]: Expose USAGE details to the front end | 2026-01-30 | 2026-06-17 | mbatista |
| [#3475](https://github.com/microsoft/agent-framework/issues/3475) | .NET: [Bug]: inconsistent usage of ag_ui_thread_id and agui_thread_id in the ChatOptions AdditionalProperties | 2026-01-28 | 2026-06-17 | nC-LBR8 |
| [#3365](https://github.com/microsoft/agent-framework/issues/3365) | .NET: [Bug]: AGUI AGUIMessage -> ChatMessage transformation issue on AGUIToolMessage | 2026-01-22 | 2026-06-17 | MaciejWarchalowski |
| [#3215](https://github.com/microsoft/agent-framework/issues/3215) | .NET: [Bug]: Copilot Studio Agent and AG-UI Protocol Integration | 2026-01-14 | 2026-06-17 | george-zhurakhivskyi8angelogordon |
| [#3033](https://github.com/microsoft/agent-framework/issues/3033) | Python: Using .Net agent framework client to connect to DevUI Responses REST API (python) | 2025-12-24 | 2026-06-17 | malv007 |
| [#3011](https://github.com/microsoft/agent-framework/issues/3011) | .NET: Unable to test handoffs workflow using DevUI | 2025-12-22 | 2026-06-17 | debarghyaroy012 |
| [#3002](https://github.com/microsoft/agent-framework/issues/3002) | .NET: Workflow as AG-UI agent does not recognise client-side tools | 2025-12-22 | 2026-06-17 | Dazfl |
| [#2988](https://github.com/microsoft/agent-framework/issues/2988) | .NET: Support dynamic agent resolution in AG-UI endpoints (MapAGUI with factory delegate) | 2025-12-20 | 2026-06-17 | mattbrailsford |
| [#2959](https://github.com/microsoft/agent-framework/issues/2959) | .NET: Azure AI Projects agent-mode silently ignores per-request tools (AG-UI/ChatOptions.Tools) | 2025-12-18 | 2026-06-17 | MaciejSzczepanskiRedslim |
| [#2911](https://github.com/microsoft/agent-framework/issues/2911) | .NET: DevUI is not supporting testing workflow which is having a sub-workflow | 2025-12-16 | 2026-06-17 | debarghyaroy012 |
| [#2702](https://github.com/microsoft/agent-framework/issues/2702) | .NET ag-ui with openAI responses api | 2025-12-08 | 2026-06-17 | jcageman |
| [#2699](https://github.com/microsoft/agent-framework/issues/2699) | .NET: AG-UI: Multi-turn tool calls replay produces invalid OpenAI tool_call history | 2025-12-08 | 2026-06-17 | emilmuller |
| [#2691](https://github.com/microsoft/agent-framework/issues/2691) | .NET: DevUI workflows require complex Chat Protocol while Python workflows are simple - output visibility gap | 2025-12-08 | 2026-06-17 | joslat |
| [#2637](https://github.com/microsoft/agent-framework/issues/2637) | .NET AG-UI: parentMessageId is serialized with a null value which breaks validation in @ag-ui/core client | 2025-12-04 | 2026-06-17 | bdelayen |
| [#2558](https://github.com/microsoft/agent-framework/issues/2558) | .NET - AG-UI Support more AG-UI event types. | 2025-12-01 | 2026-06-17 | javiercn |
| [#2555](https://github.com/microsoft/agent-framework/issues/2555) | Python: Support MCP-UI Protocol | 2025-12-01 | 2026-06-02 | perfectspr |
| [#2517](https://github.com/microsoft/agent-framework/issues/2517) | .NET: Thread persistence does not work by default in MapAGUI | 2025-11-28 | 2026-06-17 | DavidParks8 |
| [#2510](https://github.com/microsoft/agent-framework/issues/2510) | .NET: Sync AG-UI conversation history from backend | 2025-11-28 | 2026-06-17 | Kermittt |
| [#2494](https://github.com/microsoft/agent-framework/issues/2494) | .NET: AG-UI support for workflow as agent | 2025-11-27 | 2026-06-17 | kostapetan |
| [#2179](https://github.com/microsoft/agent-framework/issues/2179) | .NET: Add Support for Passing Agent Name (String) Instead of AIAgent Instance to MapAGUI Method | 2025-11-13 | 2026-06-17 | Varorbc |
| [#2109](https://github.com/microsoft/agent-framework/issues/2109) | .NET DevUI Structured Input support | 2025-11-12 | 2026-06-17 | oli-ideally |
| [#2084](https://github.com/microsoft/agent-framework/issues/2084) | .NET: DevUI limitation should be documented and addressed if possible | 2025-11-11 | 2026-06-17 | Vijay-Nirmal |
| [#2081](https://github.com/microsoft/agent-framework/issues/2081) | .NET: Support for intermediate state from Tools for AG-UI | 2025-11-11 | 2026-06-17 | anktsrkr |

