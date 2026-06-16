using AGUI.Client;
using Step01_GettingStarted.Client;

var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5001";

using var httpClient = new HttpClient();
var aguiClient = new AGUIChatClient(httpClient, baseUrl);

await SampleClient.RunAsync(aguiClient, Console.Out).ConfigureAwait(false);
