// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Console.TypeSidecars;

public sealed record ConsoleServiceTypeMetadata
{
    [JsonPropertyName("serviceType")]
    public required MetadataV2ServiceType ServiceType { get; init; }

    [JsonPropertyName("routeBase")]
    public string? RouteBase { get; init; }

    [JsonPropertyName("publications")]
    public IReadOnlyList<MetadataV2PublicationType> Publications { get; init; }
        = Array.Empty<MetadataV2PublicationType>();
}

public sealed record ConsoleLayerTypeMetadata
{
    [JsonPropertyName("resourceId")]
    public required string ResourceId { get; init; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("fieldCount")]
    public int? FieldCount { get; init; }

    [JsonPropertyName("extent")]
    public IReadOnlyList<double>? Extent { get; init; }
}

public sealed record ConsoleSavedMapTypeMetadata
{
    [JsonPropertyName("layerCount")]
    public int LayerCount { get; init; }

    [JsonPropertyName("extent")]
    public IReadOnlyList<double>? Extent { get; init; }

    [JsonPropertyName("projection")]
    public string? Projection { get; init; }
}

public sealed record ConsoleDashboardTypeMetadata
{
    [JsonPropertyName("widgetCount")]
    public int WidgetCount { get; init; }

    [JsonPropertyName("dataSourceIds")]
    public IReadOnlyList<string> DataSourceIds { get; init; } = Array.Empty<string>();
}

public sealed record ConsoleReportTypeMetadata
{
    [JsonPropertyName("templateId")]
    public string? TemplateId { get; init; }

    [JsonPropertyName("lastRunAt")]
    public DateTimeOffset? LastRunAt { get; init; }
}

public sealed record ConsoleGeneratedAppTypeMetadata
{
    [JsonPropertyName("appType")]
    public required string AppType { get; init; }

    [JsonPropertyName("sourceArtifactId")]
    public string? SourceArtifactId { get; init; }

    [JsonPropertyName("entryUrl")]
    public string? EntryUrl { get; init; }
}

public sealed record ConsoleOpenDataTypeMetadata
{
    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("formats")]
    public IReadOnlyList<string> Formats { get; init; } = Array.Empty<string>();

    [JsonPropertyName("distributionUrls")]
    public IReadOnlyList<string> DistributionUrls { get; init; } = Array.Empty<string>();
}
