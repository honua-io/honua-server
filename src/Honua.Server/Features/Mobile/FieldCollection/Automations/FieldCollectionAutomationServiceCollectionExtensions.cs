// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Mobile.FieldCollection.Automations;

/// <summary>
/// Wires the server-side FieldCollection Workflows companion (#2121): the
/// post-push trigger, configuration-backed action store, in-process dispatch
/// queue, background delivery service, and online action handlers.
/// </summary>
internal static class FieldCollectionAutomationServiceCollectionExtensions
{
    public static IServiceCollection AddFieldCollectionAutomations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<FieldCollectionAutomationOptions>()
            .Bind(configuration.GetSection(FieldCollectionAutomationOptions.SectionName));

        services.TryAddSingleton<IFieldCollectionAutomationStore, OptionsFieldCollectionAutomationStore>();

        // The channel dispatcher is a singleton shared by the trigger (writer) and
        // the background service (reader); register the concrete type once and
        // expose it through the abstraction.
        services.TryAddSingleton<ChannelFieldCollectionActionDispatcher>();
        services.TryAddSingleton<IFieldCollectionActionDispatcher>(
            sp => sp.GetRequiredService<ChannelFieldCollectionActionDispatcher>());

        services.TryAddScoped<IFieldCollectionAutomationTrigger, FieldCollectionAutomationTrigger>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFieldCollectionActionHandler, WebhookFieldCollectionActionHandler>());

        services.AddHostedService<FieldCollectionAutomationDispatchBackgroundService>();

        return services;
    }
}
