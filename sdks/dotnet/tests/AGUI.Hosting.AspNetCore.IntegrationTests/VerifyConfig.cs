using System.Runtime.CompilerServices;
using DiffEngine;

namespace AGUI.Hosting.AspNetCore.IntegrationTests;

internal static class VerifyConfig
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        VerifierSettings.DontScrubDateTimes();
        DiffRunner.Disabled = true;
    }
}
