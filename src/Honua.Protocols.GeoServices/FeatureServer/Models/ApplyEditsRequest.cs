// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class ApplyEditsRequest
{
    [JsonPropertyName("adds")]
    public GeoServicesFeature[]? Adds { get; set; }

    [JsonPropertyName("updates")]
    public GeoServicesFeature[]? Updates { get; set; }

    [JsonPropertyName("deletes")]
    public object[]? Deletes { get; set; }

    [JsonPropertyName("rollbackOnFailure")]
    public bool RollbackOnFailure { get; set; } = true;

    [JsonIgnore]
    public bool RollbackOnFailureExplicitlySet { get; set; }

    [JsonIgnore]
    public ImmutableArray<FeatureEditPrecondition> Preconditions { get; set; }

    [JsonPropertyName("useGlobalIds")]
    public bool UseGlobalIds { get; set; } = false;

    [JsonPropertyName("f")]
    public string? F { get; set; }

    [JsonPropertyName("gdbVersion")]
    public string? GdbVersion { get; set; }

    [JsonPropertyName("returnEditMoment")]
    public bool ReturnEditMoment { get; set; }

    [JsonPropertyName("attachments")]
    public object[]? Attachments { get; set; }

    [JsonPropertyName("assetMaps")]
    public object[]? AssetMaps { get; set; }

    [JsonPropertyName("async")]
    public bool Async { get; set; }

    [JsonPropertyName("useUniqueIds")]
    public bool UseUniqueIds { get; set; }

    [JsonPropertyName("editsUploadId")]
    public string? EditsUploadId { get; set; }

    [JsonPropertyName("editsUploadFormat")]
    public string? EditsUploadFormat { get; set; }

    [JsonPropertyName("datumTransformation")]
    public object? DatumTransformation { get; set; }
}

public sealed class ApplyEditsResponse
{
    [JsonPropertyName("addResults")]
    public EditResult[]? AddResults { get; set; }

    [JsonPropertyName("updateResults")]
    public EditResult[]? UpdateResults { get; set; }

    [JsonPropertyName("deleteResults")]
    public EditResult[]? DeleteResults { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("editMoment")]
    public long? EditMoment { get; set; }
}

public sealed class EditResult
{
    [JsonPropertyName("objectId")]
    public long? ObjectId { get; set; }

    [JsonPropertyName("globalId")]
    public string? GlobalId { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("error")]
    public EditError? Error { get; set; }
}

public sealed class EditError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
