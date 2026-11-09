// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class ServiceLayerEdits
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("adds")]
    public GeoServicesFeature[]? Adds { get; set; }

    [JsonPropertyName("updates")]
    public GeoServicesFeature[]? Updates { get; set; }

    [JsonPropertyName("deletes")]
    public object[]? Deletes { get; set; }

    // ArcGIS clients may send applyEdits controls in the JSON body when using the
    // service-level form. Keep them nullable so omission remains distinct from false.
    [JsonPropertyName("rollbackOnFailure")]
    public bool? RollbackOnFailure { get; set; }

    [JsonPropertyName("useGlobalIds")]
    public bool? UseGlobalIds { get; set; }

    [JsonPropertyName("returnEditMoment")]
    public bool? ReturnEditMoment { get; set; }

    [JsonPropertyName("gdbVersion")]
    public string? GdbVersion { get; set; }

    [JsonPropertyName("f")]
    public string? F { get; set; }

    [JsonPropertyName("attachments")]
    public JsonElement? Attachments { get; set; }

    // Asset-map edits are recognized so they are not silently dropped.
    [JsonPropertyName("assetMaps")]
    public JsonElement? AssetMaps { get; set; }
}

public sealed class ServiceLayerEditResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("addResults")]
    public EditResult[]? AddResults { get; set; }

    [JsonPropertyName("updateResults")]
    public EditResult[]? UpdateResults { get; set; }

    [JsonPropertyName("deleteResults")]
    public EditResult[]? DeleteResults { get; set; }

    [JsonPropertyName("editMoment")]
    public long? EditMoment { get; set; }
}
