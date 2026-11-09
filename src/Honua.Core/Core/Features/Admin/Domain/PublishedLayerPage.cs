// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

public readonly record struct PublishedLayerPageQuery(
    string ServiceName,
    Guid? ConnectionId,
    int Page,
    int PageSize,
    string? NameQuery);

public sealed class PublishedLayerPage
{
    public required IReadOnlyList<PublishedLayerSummary> Items { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public required int Total { get; init; }
}
