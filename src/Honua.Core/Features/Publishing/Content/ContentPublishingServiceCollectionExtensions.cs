// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Publishing.Content;

/// <summary>
/// Registration helpers for the content publication registry.
/// </summary>
public static class ContentPublishingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the content publication service plus an in-memory store default.
    /// Durable deployments register a Postgres-backed <see cref="IContentPublicationStore"/>
    /// later (last registration wins), and optional dependency stores
    /// (<see cref="IMetadataV2GraphProvider"/>, <see cref="IPublishedServiceStore"/>) are
    /// resolved when present.
    /// </summary>
    public static IServiceCollection AddContentPublishingServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
        services.TryAddScoped<IContentPublicationService>(sp => new ContentPublicationService(
            sp.GetRequiredService<IContentPublicationStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IMetadataV2GraphProvider>(),
            sp.GetService<IPublishedServiceStore>(),
            sp.GetRequiredService<ILogger<ContentPublicationService>>()));

        return services;
    }
}
