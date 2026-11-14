// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private DatumTransformationSelection? ResolveRequestedDatumSelection(ImportRequest request, int sourceSrid)
    {
        if (!string.IsNullOrWhiteSpace(request.DatumTransformationName))
        {
            var named = _datumTransformationCatalog?.FindByName(
                request.DatumTransformationName, sourceSrid, request.TargetSrid!.Value);
            if (named is null)
            {
                throw new InvalidOperationException("The named datumTransformation does not apply to the requested reprojection.");
            }

            return named.WithCoordinateEpoch(request.CoordinateEpoch);
        }

        if (request.DatumTransformationWkid is not { } wkid)
        {
            return sourceSrid != request.TargetSrid!.Value && _datumTransformationCatalog is not null &&
                !_datumTransformationCatalog.HasAreaOfUse(sourceSrid, request.TargetSrid!.Value) &&
                _datumTransformationCatalog.TryGetDefault(sourceSrid, request.TargetSrid!.Value, out var defaultSelection) && defaultSelection.RequiresCoordinateEpoch
                ? defaultSelection.WithCoordinateEpoch(request.CoordinateEpoch)
                : null;
        }

        if (_datumTransformationCatalog is null ||
            !_datumTransformationCatalog.TryGetByWkid(wkid, sourceSrid, request.TargetSrid!.Value, out var selection))
        {
            throw new InvalidOperationException($"datumTransformation WKID {wkid} is not supported for {sourceSrid} -> {request.TargetSrid!.Value}.");
        }

        return selection.WithCoordinateEpoch(request.CoordinateEpoch);
    }
}
