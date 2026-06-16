using Microsoft.Extensions.AI;

namespace Step10_InterruptsUserInput.Server;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUI();

        builder.Services.AddSingleton<UserInputChatClient>();
        builder.Services.AddChatClient(sp => (IChatClient)sp.GetRequiredService<UserInputChatClient>());

        var app = builder.Build();

        app.MapAGUI("/");

        app.Run();
    }
}
