// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Threading;
using System.Threading.Tasks;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface ILayerSelectionFilterTranslator
{
    Task<LayerSelectionTranslation> TranslateAsync(
        LayerSelectionFilter selection,
        MetadataV2Object resource,
        CancellationToken cancellationToken = default);
}

public sealed record LayerSelectionFilter
{
    public string? Where { get; init; }

    public string? ObjectIds { get; init; }

    public string? Geometry { get; init; }

    public string? GeometryType { get; init; }

    public string? InSr { get; init; }

    public string? SpatialRel { get; init; }

    public string? Time { get; init; }

    public string? TimeRelation { get; init; }
}

public sealed record LayerSelectionTranslation
{
    public FeatureQuery? Query { get; init; }

    public string? ErrorTitle { get; init; }

    public string? ErrorDetail { get; init; }

    public static LayerSelectionTranslation Success(FeatureQuery query) => new() { Query = query };

    public static LayerSelectionTranslation Failure(string title, string detail)
        => new() { ErrorTitle = title, ErrorDetail = detail };
}

public sealed class DagSourceSelectionException : Exception
{
    public DagSourceSelectionException(string message)
        : base(message)
    {
    }

    public DagSourceSelectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DagSourceSelectionException()
    {
    }
}
