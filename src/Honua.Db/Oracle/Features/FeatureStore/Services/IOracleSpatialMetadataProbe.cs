// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Db.Oracle.Features.FeatureStore.Services;

internal interface IOracleSpatialMetadataProbe
{
    Task<string?> GetGeometryColumnTypeAsync(
        OracleLayerMapping mapping,
        DataConnection? dataConnection,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetArcSdeVersioningColumnsAsync(
        OracleLayerMapping mapping,
        DataConnection? dataConnection,
        CancellationToken cancellationToken);
}
