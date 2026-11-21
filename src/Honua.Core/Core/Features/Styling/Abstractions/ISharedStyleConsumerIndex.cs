// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy;

namespace Honua.Core.Features.Styling.Abstractions;

public interface ISharedStyleConsumerIndex
{
    Task<SharedStyleConsumerScan> ListConsumersAsync(
        SharedStyleConsumerQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record SharedStyleConsumerQuery
{
    public required string StyleId { get; init; }

    public required int Limit { get; init; }

    // Studio rows are filtered like the Studio content lists; null is an unscoped caller.
    public TenantScopeFilter? Tenant { get; init; }
}

// Who the impact review is computed for. Consumers are Studio rows filtered like the Studio
// content lists; associations are layers the request tenant can see in the metadata graph.
public sealed record SharedStyleImpactRequest
{
    public required string StyleId { get; init; }

    public ISharedStyleConsumerIndex? ConsumerIndex { get; init; }

    public TenantScopeFilter? ConsumerTenant { get; init; }

    public string? RequestTenant { get; init; }
}

public sealed record SharedStyleConsumerScan
{
    public required IReadOnlyList<SharedStyleConsumerRow> Consumers { get; init; }

    public required bool Complete { get; init; }
}

public sealed record SharedStyleConsumerRow
{
    public required string Kind { get; init; }

    public required string ItemId { get; init; }

    public required string PackageKey { get; init; }

    public required string ContentHash { get; init; }

    public required string PublicationState { get; init; }

    public string? VersionId { get; init; }

    public string? DraftId { get; init; }

    public long? Generation { get; init; }

    public required bool IsCurrent { get; init; }
}

public sealed record SharedStyleAssociation
{
    public required string ResourceId { get; init; }

    public required string Name { get; init; }

    public string? Title { get; init; }

    public string? ServiceId { get; init; }
}
