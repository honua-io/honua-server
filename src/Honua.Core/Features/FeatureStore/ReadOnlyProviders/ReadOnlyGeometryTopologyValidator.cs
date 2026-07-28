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
/// Topology validation is a backend-assisted capability (the PostGIS
/// implementation delegates to <c>ST_IsValid</c>/<c>ST_MakeValid</c>). Read-only
/// providers never accept the edits that would need it; if a caller does invoke
/// this validator (only reachable with <c>Validation:EnableTopologyValidation</c>
/// enabled), the shared <c>GeometryValidator</c> maps the exception to a clean
/// validation failure rather than propagating it.
/// </remarks>
public sealed class ReadOnlyGeometryTopologyValidator : IGeometryTopologyValidator
{
    private readonly string _providerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyGeometryTopologyValidator"/> class.
    /// </summary>
    /// <param name="providerName">
    /// Display name of the read-only provider, used in the rejection message
    /// (for example <c>"DuckDB"</c> or <c>"MySQL/MariaDB"</c>).
    /// </param>
    public ReadOnlyGeometryTopologyValidator(string providerName)
    {
        _providerName = providerName;
    }

    /// <inheritdoc />
    public Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{_providerName} provider is read-only. Backend topology validation is not supported.");

    /// <inheritdoc />
    public Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{_providerName} provider is read-only. Geometry repair is not supported.");
}
