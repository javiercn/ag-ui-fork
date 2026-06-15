using System;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.AI;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> to configure AG-UI support.
/// </summary>
public static class AGUIServiceCollectionExtensions
{
    /// <summary>
    /// Adds AG-UI services to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to configure.</param>
    /// <returns>The <see cref="IServiceCollection"/> for method chaining.</returns>
    public static IServiceCollection AddAGUI(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
            options.SerializerOptions.TypeInfoResolverChain.Add(AGUIJsonSerializerContext.Default);
            RegisterInterruptContentTypes(options.SerializerOptions);
        });

        return services;
    }

    /// <summary>
    /// Registers interrupt content types with the specified <see cref="JsonSerializerOptions"/>.
    /// Call this method when configuring serializer options outside of <see cref="AddAGUI"/>.
    /// </summary>
    /// <param name="options">The JSON serializer options to configure.</param>
    public static void RegisterInterruptContentTypes(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddAIContentType<InterruptRequestContent>("interruptRequest");
        options.AddAIContentType<InterruptResponseContent>("interruptResponse");
    }
}
