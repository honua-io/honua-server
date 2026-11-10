// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoservicesServiceInfo
{
    public required string ServiceUrl { get; init; }

    public required string ServiceName { get; init; }

    public string? Description { get; init; }

    public int? SpatialReferenceWkid { get; init; }

    public int? MaxRecordCount { get; init; }

    public string[] Capabilities { get; init; } = [];

    public GeoservicesLayerInfo[] Layers { get; init; } = [];

    public string? Version { get; init; }

    public string[] SupportedQueryFormats { get; init; } = [];
}

public sealed record GeoservicesLayerInfo
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? GeometryType { get; init; }

    public bool HasZ { get; init; }

    public bool HasM { get; init; }

    // Null means the source did not advertise pagination, which is different from an explicit false.
    public bool? SupportsPagination { get; init; }

    public int? SpatialReferenceWkid { get; init; }

    public int? MaxRecordCount { get; init; }

    public GeoservicesFieldInfo[] Fields { get; init; } = [];

    public GeoservicesRelationshipInfo?[] Relationships { get; init; } = [];

    public string? Type { get; init; }

    public bool HasAttachments { get; init; }

    public string? GlobalIdField { get; init; }

    public double? MinScale { get; init; }

    public double? MaxScale { get; init; }

    public GeoServicesExtent? Extent { get; init; }

    public int? FeatureCount { get; init; }

    public string? DrawingInfoJson { get; init; }

    public Honua.Core.Features.Metadata.Domain.V2.MetadataV2Subtypes? Subtypes { get; init; }

    public IReadOnlyList<Honua.Core.Features.Metadata.Domain.V2.MetadataV2AttributeRule>? AttributeRules { get; init; }
}

public sealed record GeoservicesFieldInfo : FieldDefinitionBase
{
    public Honua.Core.Features.Metadata.Domain.V2.MetadataV2FieldDomain? Domain { get; init; }

    public bool IsObjectId => Type.Equals("esriFieldTypeOID", StringComparison.OrdinalIgnoreCase);

    protected override bool IsStringType()
    {
        return Type.Equals("esriFieldTypeString", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record GeoServicesExtent
{
    public double Xmin { get; init; }

    public double Ymin { get; init; }

    public double Xmax { get; init; }

    public double Ymax { get; init; }

    public int? SpatialReferenceWkid { get; init; }
}
