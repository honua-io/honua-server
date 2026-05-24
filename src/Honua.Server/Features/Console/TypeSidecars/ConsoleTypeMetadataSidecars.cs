// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Console.TypeSidecars;

/// <summary>
/// Type-specific metadata for a <c>service</c> content item.
/// </summary>
public sealed record ConsoleServiceTypeMetadata
{
    /// <summary>Service category (Map/Feature/Tile/…).</summary>
    [JsonPropertyName("serviceType")]
    public required MetadataV2ServiceType ServiceType { get; init; }

    /// <summary>Root URL segment under which the service publishes endpoints.</summary>
    [JsonPropertyName("routeBase")]
    public string? RouteBase { get; init; }

    /// <summary>Publication channels exposed by the service.</summary>
    [JsonPropertyName("publications")]
    public IReadOnlyList<MetadataV2PublicationType> Publications { get; init; }
        = Array.Empty<MetadataV2PublicationType>();
}

/// <summary>
/// Type-specific metadata for a <c>layer</c> content item.
/// </summary>
public sealed record ConsoleLayerTypeMetadata
{
    /// <summary>Backing resource identifier.</summary>
    [JsonPropertyName("resourceId")]
    public required string ResourceId { get; init; }

    /// <summary>Geometry classification ("point", "line", "polygon", "raster", "table", …).</summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>Field count for tabular/vector resources.</summary>
    [JsonPropertyName("fieldCount")]
    public int? FieldCount { get; init; }

    /// <summary>Extent as [minX, minY, maxX, maxY] in EPSG:4326 longitude/latitude.</summary>
    [JsonPropertyName("extent")]
    public IReadOnlyList<double>? Extent { get; init; }
}

/// <summary>
/// Type-specific metadata for a <c>saved-map</c> content item.
/// </summary>
public sealed record ConsoleSavedMapTypeMetadata
{
    /// <summary>Number of layers in the saved composition.</summary>
    [JsonPropertyName("layerCount")]
    public int LayerCount { get; init; }

    /// <summary>Extent as [minX, minY, maxX, maxY] in EPSG:4326 longitude/latitude.</summary>
    [JsonPropertyName("extent")]
    public IReadOnlyList<double>? Extent { get; init; }

    /// <summary>Display CRS identifier (e.g. "EPSG:3857").</summary>
    [JsonPropertyName("projection")]
    public string? Projection { get; init; }
}

/// <summary>
/// Type-specific metadata for a <c>dashboard</c> content item.
/// </summary>
public sealed record ConsoleDashboardTypeMetadata
{
    /// <summary>Number of widgets on the dashboard.</summary>
    [JsonPropertyName("widgetCount")]
    public int WidgetCount { get; init; }

    /// <summary>Identifiers of the data sources the dashboard binds against.</summary>
    [JsonPropertyName("dataSourceIds")]
    public IReadOnlyList<string> DataSourceIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Type-specific metadata for a <c>report</c> content item.
/// </summary>
public sealed record ConsoleReportTypeMetadata
{
    /// <summary>Template identifier the report is generated from.</summary>
    [JsonPropertyName("templateId")]
    public string? TemplateId { get; init; }

    /// <summary>Timestamp of the most recent successful run.</summary>
    [JsonPropertyName("lastRunAt")]
    public DateTimeOffset? LastRunAt { get; init; }
}

/// <summary>
/// Type-specific metadata for a <c>generated-app</c> content item.
/// </summary>
public sealed record ConsoleGeneratedAppTypeMetadata
{
    /// <summary>Generated application kind ("dashboard", "viewer", "form", …).</summary>
    [JsonPropertyName("appType")]
    public required string AppType { get; init; }

    /// <summary>Identifier of the Studio artifact the app was generated from.</summary>
    [JsonPropertyName("sourceArtifactId")]
    public string? SourceArtifactId { get; init; }

    /// <summary>Public entry URL, when the app is hosted.</summary>
    [JsonPropertyName("entryUrl")]
    public string? EntryUrl { get; init; }
}

/// <summary>
/// Type-specific metadata for an <c>open-data</c> content item.
/// </summary>
public sealed record ConsoleOpenDataTypeMetadata
{
    /// <summary>SPDX license identifier ("CC-BY-4.0", "ODbL-1.0", …).</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Distribution formats ("geojson", "csv", "geoparquet", …).</summary>
    [JsonPropertyName("formats")]
    public IReadOnlyList<string> Formats { get; init; } = Array.Empty<string>();

    /// <summary>Public distribution URLs.</summary>
    [JsonPropertyName("distributionUrls")]
    public IReadOnlyList<string> DistributionUrls { get; init; } = Array.Empty<string>();
}
