// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Request model for a single layer's edits in a service-level applyEdits call
/// </summary>
public sealed class ServiceLayerEdits
{
    /// <summary>
    /// Layer ID to apply edits to
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Features to add to the layer
    /// </summary>
    [JsonPropertyName("adds")]
    public GeoServicesFeature[]? Adds { get; set; }

    /// <summary>
    /// Features to update in the layer
    /// </summary>
    [JsonPropertyName("updates")]
    public GeoServicesFeature[]? Updates { get; set; }

    /// <summary>
    /// Feature IDs to delete from the layer
    /// </summary>
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
}

/// <summary>
/// Response model for service-level applyEdits containing per-layer results
/// </summary>
public sealed class ServiceApplyEditsResponse
{
    /// <summary>
    /// Per-layer edit results
    /// </summary>
    [JsonPropertyName("editResults")]
    public ServiceLayerEditResult[]? EditResults { get; set; }
}

/// <summary>
/// Edit results for a single layer in a service-level applyEdits response
/// </summary>
public sealed class ServiceLayerEditResult
{
    /// <summary>
    /// Layer ID the results apply to
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Results of add operations
    /// </summary>
    [JsonPropertyName("addResults")]
    public EditResult[]? AddResults { get; set; }

    /// <summary>
    /// Results of update operations
    /// </summary>
    [JsonPropertyName("updateResults")]
    public EditResult[]? UpdateResults { get; set; }

    /// <summary>
    /// Results of delete operations
    /// </summary>
    [JsonPropertyName("deleteResults")]
    public EditResult[]? DeleteResults { get; set; }
}
