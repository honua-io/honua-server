// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Domain;

namespace Honua.Core.Features.Publishing.Abstractions;

public interface IPublishIntentStore
{
    Task<bool> TryCreateAsync(
        PublishIntent intent,
        CancellationToken cancellationToken = default);

    Task<PublishIntent?> GetAsync(
        string intentId,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        PublishIntent intent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublishIntent>> ListBySourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default);
}

public interface IPublishedServiceStore
{
    Task<bool> TryCreateAsync(
        PublishedServiceRecord service,
        CancellationToken cancellationToken = default);

    Task<PublishedServiceRecord?> GetAsync(
        string serviceId,
        CancellationToken cancellationToken = default);

    Task<PublishedServiceRecord?> FindByPublicIdAsync(
        string serviceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> FindCollidingPublicIdsAsync(
        IReadOnlyList<string> candidateServiceIds,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        PublishedServiceRecord service,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublishedServiceRecord>> ListActiveAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublishedServiceRecord>> ListBySourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default);
}

public interface IPublishExecutor
{
    Task<PublishedServiceRecord> ExecuteAsync(
        PublishIntent intent,
        CancellationToken cancellationToken = default);
}
