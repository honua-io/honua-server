// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin.Models;

public sealed class PublishLayerRequest
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public Honua.Core.Features.Metadata.Domain.V2.MetadataV2FolderSelection FolderId { get; init; }

    public string? PublicName { get; init; }

    public PublishLayerRequest()
    {
    }

    [JsonConstructor]
    public PublishLayerRequest(bool enabled = true)
    {
        // .NET 10 source generation overwrites omitted init-only initializers.
        // A constructor parameter retains the wire default without losing explicit false.
        Enabled = enabled;
    }

    public bool CreateEditableCopy { get; init; }

    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Schema { get; init; }

    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Table { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string LayerName { get; init; }

    [StringLength(1000)]
    public string? Description { get; init; }

    [StringLength(LayerSourceGovernance.MaxLicenseLength)]
    public string? License { get; init; }

    [StringLength(LayerSourceGovernance.MaxAttributionLength)]
    public string? Attribution { get; init; }

    [StringLength(LayerSourceGovernance.MaxPublisherLength)]
    public string? Publisher { get; init; }

    [StringLength(LayerSourceGovernance.MaxUrlLength)]
    public string? LicenseUrl { get; init; }

    [StringLength(LayerSourceGovernance.MaxUrlLength)]
    public string? SourceUrl { get; init; }

    [StringLength(64)]
    public string? GeometryColumn { get; init; }

    [StringLength(64)]
    public string? GeometryType { get; init; }

    public bool HasZ { get; init; }

    public bool HasM { get; init; }

    [Range(0, int.MaxValue)]
    public int? Srid { get; init; }

    [StringLength(64)]
    public string? PrimaryKey { get; init; }

    [StringLength(63)]
    public string? GlobalIdField { get; init; }

    [StringLength(63)]
    public string? TemporalColumn { get; init; }

    public bool SupportsAttachments { get; init; }

    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, MetadataV2FieldDomain>? FieldDomains { get; init; }

    public MetadataV2Subtypes? Subtypes { get; init; }

    public IReadOnlyList<MetadataV2AttributeRule>? AttributeRules { get; init; }

    [StringLength(64)]
    public string? ServiceName { get; init; }

    [StringLength(128, MinimumLength = 1)]
    public string? Namespace { get; init; }

    public bool Enabled { get; init; } = true;

    [StringLength(16)]
    public string? StorageMode { get; init; }

    public IReadOnlyList<string>? Capabilities { get; init; }
}

public sealed class LayerPublishAcceptedResponse
{
    public required string JobId { get; init; }

    public required string StatusUrl { get; init; }

    public required long RowsTotal { get; init; }

    public required string Status { get; init; }
}

public sealed class LayerPublishJobStatusResponse
{
    public required string JobId { get; init; }

    public required string Status { get; init; }

    public required long RowsMaterialized { get; init; }

    public required long RowsTotal { get; init; }

    public int? StorageLayerId { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class ValidateTablePublishRequest
{
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Schema { get; init; }

    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Table { get; init; }

    [StringLength(128, MinimumLength = 1)]
    public string? LayerName { get; init; }

    [StringLength(64)]
    public string? ServiceName { get; init; }

    [Range(0, int.MaxValue)]
    public int? TargetSrid { get; init; }

    [StringLength(64)]
    public string? GeometryColumn { get; init; }

    [StringLength(64)]
    public string? PrimaryKey { get; init; }

    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
}

public sealed class LayerEnabledRequest
{
    public bool Enabled { get; init; }
}
