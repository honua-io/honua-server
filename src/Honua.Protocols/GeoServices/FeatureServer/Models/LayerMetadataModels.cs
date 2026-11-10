// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class EditFieldsInfo
{
    public string? CreatorField { get; init; }

    public string? CreationDateField { get; init; }

    public string? EditorField { get; init; }

    public string? EditDateField { get; init; }
}

public sealed class EditingInfo
{
    public long? LastEditDate { get; init; }
}

public sealed class UniqueIdFieldInfo
{
    public required string Name { get; init; }

    public bool IsSystemMaintained { get; init; }
}

public sealed class AdvancedQueryCapabilities
{
    public bool UseStandardizedQueries { get; init; } = true;

    public bool SupportsStatistics { get; init; } = true;

    public bool SupportsPercentileStatistics { get; init; }

    public bool SupportsHavingClause { get; init; }

    public bool SupportsOrderBy { get; init; } = true;

    public bool SupportsDistinct { get; init; } = true;

    public bool SupportsCountDistinct { get; init; } = true;

    public bool SupportsPagination { get; init; } = true;

    public bool SupportsLod { get; init; }

    public bool SupportsQueryWithLodSR { get; init; }

    public bool SupportsTrueCurve { get; init; }

    public bool SupportsReturningGeometryCentroid { get; init; }

    public bool SupportsReturningQueryExtent { get; init; } = true;

    public bool SupportsQueryWithDistance { get; init; } = true;

    public bool SupportsQueryWithDatumTransformation { get; init; } = true;

    public bool SupportsSqlExpression { get; init; } = true;

    public bool SupportsTopFeaturesQuery { get; init; }

    public bool SupportsBatchEditing { get; init; } = true;

    public bool SupportsQueryAnalytic { get; init; }

    // The nested supportsQueryAttachments flag must match the layer root, or the ArcGIS Maps SDK for JavaScript refuses queryAttachments on a layer that advertises attachments.
    public bool SupportsQueryAttachments { get; init; }
}

public sealed class FeatureTemplate
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? DrawingTool { get; init; }

    public object? Prototype { get; init; }
}

public sealed class FeatureTemplatePrototype
{
    [System.Text.Json.Serialization.JsonPropertyName("attributes")]
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement> Attributes { get; init; }
        = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
}
