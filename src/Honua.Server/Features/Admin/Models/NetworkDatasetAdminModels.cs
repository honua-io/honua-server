// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Wire shape returned for a network dataset by the admin editing surface (#1882).
/// </summary>
public sealed class NetworkDatasetDto
{
    /// <summary>Stable dataset id (lowercase slug).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Schema-qualified pgRouting edge table.</summary>
    public string EdgeTable { get; set; } = string.Empty;

    /// <summary>Schema-qualified pgRouting vertex table.</summary>
    public string VertexTable { get; set; } = string.Empty;

    /// <summary>Topology SRID.</summary>
    public int Srid { get; set; }

    /// <summary>Lifecycle status (active/building/disabled).</summary>
    public string Status { get; set; } = "active";

    /// <summary>Monotonic topology version.</summary>
    public int TopologyVersion { get; set; }

    /// <summary>Whether the topology needs a rebuild after edits (read-only here).</summary>
    public bool NeedsRebuild { get; set; }

    /// <summary>Optional operator note.</summary>
    public string? Description { get; set; }

    /// <summary>Registration timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last-update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Admin identity that registered the dataset.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Admin identity that last updated the dataset.</summary>
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Request body to register a new network dataset.
/// </summary>
public sealed class RegisterNetworkDatasetRequest
{
    /// <summary>Stable dataset id (lowercase slug).</summary>
    public string? Id { get; set; }

    /// <summary>Human-readable display name.</summary>
    public string? Name { get; set; }

    /// <summary>Schema-qualified pgRouting edge table.</summary>
    public string? EdgeTable { get; set; }

    /// <summary>Schema-qualified pgRouting vertex table.</summary>
    public string? VertexTable { get; set; }

    /// <summary>Topology SRID. Defaults to 4326 when omitted.</summary>
    public int? Srid { get; set; }

    /// <summary>Lifecycle status. Defaults to <c>active</c> when omitted.</summary>
    public string? Status { get; set; }

    /// <summary>Optional operator note.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Request body to update a network dataset. Omitted fields keep their current value;
/// only the supplied fields are changed. The id is taken from the route, not the body.
/// </summary>
public sealed class UpdateNetworkDatasetRequest
{
    /// <summary>New display name, or null to keep the current one.</summary>
    public string? Name { get; set; }

    /// <summary>New edge table, or null to keep the current one.</summary>
    public string? EdgeTable { get; set; }

    /// <summary>New vertex table, or null to keep the current one.</summary>
    public string? VertexTable { get; set; }

    /// <summary>New SRID, or null to keep the current one.</summary>
    public int? Srid { get; set; }

    /// <summary>New status, or null to keep the current one.</summary>
    public string? Status { get; set; }

    /// <summary>New description, or null to keep the current one.</summary>
    public string? Description { get; set; }

    /// <summary>When true, clears the description (overrides <see cref="Description"/>).</summary>
    public bool? ClearDescription { get; set; }
}
