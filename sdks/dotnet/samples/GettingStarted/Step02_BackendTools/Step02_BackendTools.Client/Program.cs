using AGUI.Client;
using Step02_BackendTools.Client;

var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5002";

using var httpClient = new HttpClient();
var aguiClient = new AGUIChatClient(httpClient, baseUrl);

await SampleClient.RunAsync(aguiClient, Console.Out).ConfigureAwait(false);
