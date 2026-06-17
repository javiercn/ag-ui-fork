using AGUI.Hosting.AspNetCore.IntegrationTests;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAGUI();
builder.Services.AddSingleton<DelegatingStreamingChatClient>();
builder.Services.AddChatClient(sp => sp.GetRequiredService<DelegatingStreamingChatClient>())
    .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true);

var app = builder.Build();
app.MapAGUI("/agui");
app.Run();

public partial class Program { }
