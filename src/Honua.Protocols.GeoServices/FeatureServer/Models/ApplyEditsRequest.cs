// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Request model for the applyEdits endpoint
/// </summary>
public sealed class ApplyEditsRequest
{
    /// <summary>
    /// Array of features to add
    /// </summary>
    [JsonPropertyName("adds")]
    public GeoServicesFeature[]? Adds { get; set; }

    /// <summary>
    /// Array of features to update
    /// </summary>
    [JsonPropertyName("updates")]
    public GeoServicesFeature[]? Updates { get; set; }

    /// <summary>
    /// Array of objectIds to delete
    /// </summary>
    [JsonPropertyName("deletes")]
    public object[]? Deletes { get; set; }

    /// <summary>
    /// Whether to rollback all changes on failure.
    /// Default is false for applyEdits, true for standalone endpoints (addFeatures, updateFeatures, deleteFeatures).
    /// </summary>
    [JsonPropertyName("rollbackOnFailure")]
    public bool RollbackOnFailure { get; set; } = false;

    /// <summary>
    /// Tracks whether rollbackOnFailure was explicitly provided in the request.
    /// Used by standalone endpoints to apply the correct default.
    /// </summary>
    [JsonIgnore]
    public bool RollbackOnFailureExplicitlySet { get; set; }

    /// <summary>
    /// Whether to use global IDs
    /// </summary>
    [JsonPropertyName("useGlobalIds")]
    public bool UseGlobalIds { get; set; } = false;

    /// <summary>
    /// Requested response format.
    /// </summary>
    [JsonPropertyName("f")]
    public string? F { get; set; }

    /// <summary>
    /// Geodatabase version name.
    /// </summary>
    [JsonPropertyName("gdbVersion")]
    public string? GdbVersion { get; set; }

    /// <summary>
    /// Whether to include server edit timestamp metadata in response.
    /// </summary>
    [JsonPropertyName("returnEditMoment")]
    public bool ReturnEditMoment { get; set; }

    /// <summary>
    /// Optional attachment edits payload (not yet implemented).
    /// </summary>
    [JsonPropertyName("attachments")]
    public object[]? Attachments { get; set; }
}

/// <summary>
/// Response model for the applyEdits endpoint
/// </summary>
public sealed class ApplyEditsResponse
{
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

    /// <summary>
    /// Whether the entire transaction succeeded
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;
}

/// <summary>
/// Result of an individual edit operation
/// </summary>
public sealed class EditResult
{
    /// <summary>
    /// Object ID of the affected feature
    /// </summary>
    [JsonPropertyName("objectId")]
    public long? ObjectId { get; set; }

    /// <summary>
    /// Global ID of the affected feature (if applicable)
    /// </summary>
    [JsonPropertyName("globalId")]
    public string? GlobalId { get; set; }

    /// <summary>
    /// Whether this operation succeeded
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    /// <summary>
    /// Error information if operation failed
    /// </summary>
    [JsonPropertyName("error")]
    public EditError? Error { get; set; }
}

/// <summary>
/// Error information for failed edit operations
/// </summary>
public sealed class EditError
{
    /// <summary>
    /// Error code
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    /// Error description
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
