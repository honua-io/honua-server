// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Protocols.GeoServices.FeatureServer.Services;

namespace Honua.Protocols.GeoServices;

internal static class GeoServicesDatumTransformationResolver
{
    public static bool TryResolve(
        IDatumTransformationCatalog catalog,
        string? datumTransformationValue,
        int fromSrid,
        int toSrid,
        out DatumTransformationSelection? selection,
        [NotNullWhen(false)] out string? errorMessage,
        DatumAreaEnvelope? envelope = null)
        => TryResolveWithDirection(
            catalog,
            datumTransformationValue,
            transformForwardOverride: null,
            fromSrid,
            toSrid,
            out selection,
            out errorMessage,
            envelope);

    public static bool TryResolveWithDirection(
        IDatumTransformationCatalog catalog,
        string? datumTransformationValue,
        bool? transformForwardOverride,
        int fromSrid,
        int toSrid,
        out DatumTransformationSelection? selection,
        [NotNullWhen(false)] out string? errorMessage,
        DatumAreaEnvelope? envelope = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        selection = null;
        errorMessage = null;

        // No reprojection requested, or output equals the input SRID: nothing to do when
        // the client did not request a transformation. An explicit WKID must still be
        // parsed and validated: projected CRSs can share a geodetic base, and silently
        // accepting a transformation that does not apply to that base pair is incorrect.
        if (fromSrid == toSrid && string.IsNullOrWhiteSpace(datumTransformationValue))
        {
            return true;
        }

        if (!DatumTransformationParser.TryParse(datumTransformationValue, out var request, out var parseError))
        {
            errorMessage = parseError;
            return false;
        }

        if (request is { } requested)
        {
            if (transformForwardOverride is { } overrideValue)
            {
                requested = requested with
                {
                    TransformForward = overrideValue,
                    TransformForwardSpecified = true
                };
            }

            // A client explicitly requested a transformation — it must be honored exactly
            // or rejected; never silently substituted.
            if (!catalog.TryGetByWkid(requested.Wkid, fromSrid, toSrid, out var resolved))
            {
                errorMessage =
                    $"datumTransformation WKID {requested.Wkid.ToString(CultureInfo.InvariantCulture)} is not supported "
                    + $"for the {fromSrid.ToString(CultureInfo.InvariantCulture)} -> "
                    + $"{toSrid.ToString(CultureInfo.InvariantCulture)} reprojection.";
                return false;
            }

            // TryGetByWkid returns a selection already oriented from source to target. An
            // explicitly supplied direction must agree with that orientation; toggling it
            // here would invert reverse pairs a second time.
            if (requested.TransformForwardSpecified &&
                requested.TransformForward != resolved.TransformForward)
            {
                errorMessage =
                    $"datumTransformation WKID {requested.Wkid.ToString(CultureInfo.InvariantCulture)} with "
                    + $"transformForward={requested.TransformForward.ToString().ToLowerInvariant()} does not apply "
                    + $"to the {fromSrid.ToString(CultureInfo.InvariantCulture)} -> "
                    + $"{toSrid.ToString(CultureInfo.InvariantCulture)} reprojection.";
                return false;
            }

            selection = resolved;
            return true;
        }

        // No client choice. An envelope selects the single grid whose area contains it.
        // No match, or more than one match, leaves the pipeline unset so PROJ's
        // 2-argument transform is used instead of the CONUS default.
        if (envelope is { } area)
        {
            if (catalog.TryGetForEnvelope(fromSrid, toSrid, area.West, area.South, area.East, area.North, out var areaSelection))
            {
                selection = areaSelection;
            }
            else if (!catalog.HasAreaOfUse(fromSrid, toSrid)
                && catalog.TryGetDefault(fromSrid, toSrid, out var fallback))
            {
                selection = fallback;
            }

            return true;
        }

        if (catalog.TryGetDefault(fromSrid, toSrid, out var defaultSelection))
        {
            selection = defaultSelection;
        }

        return true;
    }
}
