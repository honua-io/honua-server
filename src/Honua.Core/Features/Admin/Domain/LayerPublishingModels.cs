// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Admin.Domain;

public sealed class LayerPublishRequest
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public Honua.Core.Features.Metadata.Domain.V2.MetadataV2FolderSelection FolderId { get; init; }

    public string? PublicName { get; init; }

    public bool CreateEditableCopy { get; init; }

    public bool AllowEmptyTable { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public required string LayerName { get; init; }

    public string? Description { get; init; }

    public string? GeometryColumn { get; init; }

    public string? GeometryType { get; init; }

    public bool HasZ { get; init; }

    public bool HasM { get; init; }

    public int? Srid { get; init; }

    public double? StorageCrsCoordinateEpoch { get; init; }

    public string? PrimaryKey { get; init; }

    public string? GlobalIdField { get; init; }

    public bool SupportsAttachments { get; init; }

    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    public string? ServiceName { get; init; }

    // A grouping namespace requires a trusted tenant context; null keeps the legacy unscoped publication, and an existing service cannot be retagged.
    public string? Namespace { get; init; }

    public Guid? ConnectionId { get; init; }

    public bool Enabled { get; init; } = true;

    public LayerSourceGovernance? SourceGovernance { get; init; }

    public IReadOnlyDictionary<string, Honua.Core.Features.Metadata.Domain.V2.MetadataV2FieldDomain> FieldDomains { get; init; }
        = new Dictionary<string, Honua.Core.Features.Metadata.Domain.V2.MetadataV2FieldDomain>(StringComparer.OrdinalIgnoreCase);

    public Honua.Core.Features.Metadata.Domain.V2.MetadataV2Subtypes? Subtypes { get; init; }

    public IReadOnlyList<Honua.Core.Features.Metadata.Domain.V2.MetadataV2AttributeRule>? AttributeRules { get; init; }

    public LayerStorageMode StorageMode { get; init; } = LayerStorageMode.Source;

    public IReadOnlyList<string>? Capabilities { get; init; }

    public string? ConnectionEndpointFingerprint { get; set; }

    // Source evidence is revalidated on the server and is never read from an HTTP body.
    [JsonIgnore]
    public VerifiedCommittedSinkPublication? CommittedSinkPublication { get; set; }
}

public enum LayerStorageMode
{
    // Features are read from the live source table, and edits are refused because the server cannot write through that table.
    Source,

    Managed
}

public sealed class TablePublishValidationRequest
{
    public bool AllowEmptyTable { get; init; }

    public bool ManagedStore { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public string? LayerName { get; init; }

    public string? ServiceName { get; init; }

    public int? TargetSrid { get; init; }

    public string? GeometryColumn { get; init; }

    public string? PrimaryKey { get; init; }

    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    public string? ConnectionEndpointFingerprint { get; set; }

    [JsonIgnore]
    public VerifiedCommittedSinkPublication? CommittedSinkPublication { get; set; }
}

public sealed class TablePublishValidationResult
{
    public bool IsValid { get; init; }

    public required string Status { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public required string ServiceName { get; init; }

    public string? LayerName { get; init; }

    public string? GeometryColumn { get; init; }

    public string? GeometryType { get; init; }

    public string? PrimaryKey { get; init; }

    public string ObjectIdStrategy { get; init; } = "unresolved";

    public int? SourceSrid { get; init; }

    public int? ServiceSrid { get; init; }

    public int? TargetSrid { get; init; }

    public long? EstimatedRows { get; init; }

    public long? FeatureCount { get; init; }

    public long? NullGeometryCount { get; init; }

    public long? InvalidGeometryCount { get; init; }

    public TablePublishValidationField[] Fields { get; init; } = [];

    public TablePublishValidationCheck[] Checks { get; init; } = [];
}

public sealed class TablePublishValidationField
{
    public required string Name { get; init; }

    public required string DataType { get; init; }

    public required string FieldType { get; init; }

    public bool IsNullable { get; init; }

    public bool IsPrimaryKey { get; init; }

    public bool IsSelected { get; init; }

    public int? MaxLength { get; init; }
}

public sealed class TablePublishValidationCheck
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string Message { get; init; }

    public string? Expected { get; init; }

    public string? Actual { get; init; }
}

public sealed class PublishedLayerSummary
{
    public required int LayerId { get; init; }

    public required string LayerName { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public string? Description { get; init; }

    public string? License { get; init; }

    public string? Attribution { get; init; }

    public string? Publisher { get; init; }

    public string? LicenseUrl { get; init; }

    public string? SourceUrl { get; init; }

    public required string GeometryType { get; init; }

    public int Srid { get; init; }

    public string? PrimaryKey { get; init; }

    public int FieldCount { get; init; }

    public bool Enabled { get; init; }

    public required string ServiceName { get; init; }

    public long? MetadataRevision { get; init; }

    public string? StorageMode { get; init; }

    public IReadOnlyList<string>? Capabilities { get; init; }
}

public sealed class LayerExtentRefreshResult
{
    public required string ServiceName { get; init; }

    public int RefreshedLayerCount { get; init; }

    public int LayersWithExtent { get; init; }

    public int LayersWithoutExtent { get; init; }

    public bool ServiceExtentUpdated { get; init; }

    public IReadOnlyList<LayerExtentRefreshLayerResult> Layers { get; init; } = [];
}

public sealed class LayerExtentRefreshLayerResult
{
    public int LayerId { get; init; }

    public required string LayerName { get; init; }

    public bool HasExtent { get; init; }

    public int? ExtentSrid { get; init; }
}

public sealed class MaterializedFeatureRefreshResult
{
    public int LayerId { get; init; }

    public required string LayerName { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public int MaterializedFeatureCount { get; init; }
}

public enum LayerPublishingErrorKind
{
    Validation,

    NotFound,

    Conflict,

    Unknown
}

public sealed class LayerPublishingException : Exception
{
    public LayerPublishingErrorKind ErrorKind { get; }

    public int? LayerId { get; }

    public LayerPublishingException(LayerPublishingErrorKind errorKind, string message, int? layerId = null)
        : base(message)
    {
        ErrorKind = errorKind;
        LayerId = layerId;
    }
}
