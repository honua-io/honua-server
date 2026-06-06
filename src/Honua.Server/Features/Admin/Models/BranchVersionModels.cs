// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request body for registering a named branch version against a feature service layer.
/// </summary>
public sealed class BranchVersionCreateRequest
{
    /// <summary>
    /// Branch version name supplied to GeoServices clients via the <c>gdbVersion</c>
    /// parameter. Must not be a DEFAULT alias.
    /// </summary>
    public string? VersionName { get; set; }

    /// <summary>
    /// Service-local public layer id the branch version is forked from.
    /// </summary>
    public int LayerId { get; set; }
}

/// <summary>
/// API representation of a registered branch version.
/// </summary>
public sealed class BranchVersionResponse
{
    /// <summary>
    /// Feature service the branch version belongs to.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Branch version name.
    /// </summary>
    public required string VersionName { get; init; }

    /// <summary>
    /// Base (DEFAULT) storage layer id the branch was forked from.
    /// </summary>
    public required int BaseLayerId { get; init; }

    /// <summary>
    /// Synthetic storage layer id that isolates the branch's feature rows from DEFAULT.
    /// </summary>
    public required int BranchLayerId { get; init; }

    /// <summary>
    /// Timestamp when the branch version was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// List response for registered branch versions on a service.
/// </summary>
public sealed class BranchVersionListResponse
{
    /// <summary>
    /// Feature service the branch versions belong to.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Registered branch versions for the service.
    /// </summary>
    public required BranchVersionResponse[] Versions { get; init; }
}
