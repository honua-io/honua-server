// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// Geometry topology validator that rejects all operations with
/// <see cref="NotSupportedException"/>. Registered by read-only feature providers
/// (DuckDB, MySQL/MariaDB) so DI consumers that require
/// <see cref="IGeometryTopologyValidator"/> (the FeatureServer edit pipeline's
/// geometry validator) can activate while the slice remains read/query-only.
/// Without a registration, any edit request against a read-only-primary host
/// failed with an opaque DI resolution 500 instead of the documented clean
/// write-rejection (honua-server#2983).
/// </summary>
/// <remarks>
/// <para>
/// Topology validation is a backend-assisted capability (the PostGIS
/// implementation delegates to <c>ST_IsValid</c>/<c>ST_MakeValid</c>), and a
/// read-only backend cannot perform it. This placeholder therefore <em>passes</em>
/// rather than throwing: the shared <c>GeometryValidator</c> converts any
/// exception from a topology validator into a per-feature validation failure, so
/// a throwing placeholder would make a geometry-bearing add fail geometry
/// validation and leave <c>ExecuteEdits</c> with an empty write set — the request
/// would return an Esri-style validation response and never reach
/// <c>ReadOnlyFeatureWriter</c>, masking the documented 405 read-only rejection.
/// </para>
/// <para>
/// Passing here is safe precisely because the provider is read-only: no edit can
/// ever be persisted, so skipping the backend topology assist cannot admit invalid
/// geometry. Structural WKB validation still runs ahead of this layer, and the
/// write itself is rejected by <c>ReadOnlyFeatureWriter</c> with
/// <see cref="NotSupportedException"/>, which the FeatureServer edits handler maps
/// to a clean 405 (honua-server#2983).
/// </para>
/// </remarks>
public sealed class ReadOnlyGeometryTopologyValidator : IGeometryTopologyValidator
{
    private readonly string _providerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyGeometryTopologyValidator"/> class.
    /// </summary>
    /// <param name="providerName">
    /// Display name of the read-only provider, used in the repair-result message
    /// (for example <c>"DuckDB"</c> or <c>"MySQL/MariaDB"</c>).
    /// </param>
    public ReadOnlyGeometryTopologyValidator(string providerName)
    {
        _providerName = providerName;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always succeeds: the read-only backend performs no topology check, and the
    /// edit that would consume the result is rejected by the writer regardless.
    /// </remarks>
    public Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryValidationResult.Success());

    /// <inheritdoc />
    /// <remarks>
    /// Unreachable in practice (repair only runs after a topology failure, and
    /// <see cref="ValidateTopologyAsync"/> never fails here). Reports a clean
    /// failure rather than throwing so the shared validator does not have to
    /// absorb an exception.
    /// </remarks>
    public Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryRepairResult.Failed(
            $"{_providerName} provider is read-only. Geometry repair is not supported."));
}
