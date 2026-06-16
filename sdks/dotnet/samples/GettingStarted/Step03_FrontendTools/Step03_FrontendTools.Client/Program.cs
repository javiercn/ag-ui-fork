using AGUI.Client;
using Step03_FrontendTools.Client;

var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5003";

using var httpClient = new HttpClient();
var aguiClient = new AGUIChatClient(httpClient, baseUrl);

await SampleClient.RunAsync(aguiClient, Console.Out).ConfigureAwait(false);
