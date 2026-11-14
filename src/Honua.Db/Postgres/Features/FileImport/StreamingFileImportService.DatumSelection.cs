// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static DatumTransformationSelection BindImportCoordinateEpoch(
        DatumTransformationSelection selection, double? coordinateEpoch)
    {
        try
        {
            return selection.WithCoordinateEpoch(coordinateEpoch);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
    }

    private DatumTransformationSelection? ResolveRequestedDatumSelection(ImportRequest request, int sourceSrid, int targetSrid)
    {
        if (!string.IsNullOrWhiteSpace(request.DatumTransformationName))
        {
            var named = _datumTransformationCatalog?.FindByName(
                request.DatumTransformationName, sourceSrid, targetSrid);
            if (named is null)
            {
                throw new InvalidOperationException("The named datumTransformation does not apply to the requested reprojection.");
            }

            return BindImportCoordinateEpoch(named, request.CoordinateEpoch);
        }

        if (request.DatumTransformationWkid is not { } wkid)
        {
            return sourceSrid != targetSrid && _datumTransformationCatalog is not null &&
                !_datumTransformationCatalog.HasAreaOfUse(sourceSrid, targetSrid) &&
                _datumTransformationCatalog.TryGetDefault(sourceSrid, targetSrid, out var defaultSelection) && defaultSelection.RequiresCoordinateEpoch
                ? BindImportCoordinateEpoch(defaultSelection, request.CoordinateEpoch)
                : null;
        }

        if (_datumTransformationCatalog is null ||
            !_datumTransformationCatalog.TryGetByWkid(wkid, sourceSrid, targetSrid, out var selection))
        {
            throw new InvalidOperationException($"datumTransformation WKID {wkid} is not supported for {sourceSrid} -> {targetSrid}.");
        }

        return BindImportCoordinateEpoch(selection, request.CoordinateEpoch);
    }
}
