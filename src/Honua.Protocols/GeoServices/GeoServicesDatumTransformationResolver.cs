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
    {
        var result = Resolve(catalog, new ResolutionRequest
        {
            Value = datumTransformationValue,
            FromSrid = fromSrid,
            ToSrid = toSrid,
            Envelope = envelope
        });
        selection = result.Selection;
        errorMessage = result.Error;
        return errorMessage is null;
    }

    public static Resolution Resolve(IDatumTransformationCatalog catalog, ResolutionRequest parameters)
    {
        var operation = ResolveOperation(catalog, parameters);
        if (operation.Error is not null)
        {
            return operation;
        }

        double? observationEpoch = null;
        if (!string.IsNullOrWhiteSpace(parameters.CoordinateEpoch))
        {
            if (!double.TryParse(parameters.CoordinateEpoch, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEpoch)
                || !double.IsFinite(parsedEpoch))
            {
                return new Resolution(null, "coordinateEpoch must be a finite decimal year.");
            }

            observationEpoch = parsedEpoch;
        }

        try
        {
            return new Resolution(operation.Selection?.WithCoordinateEpoch(observationEpoch));
        }
        catch (ArgumentException exception)
        {
            return new Resolution(null, exception.Message);
        }
    }

    internal sealed record ResolutionRequest
    {
        public string? Value { get; init; }
        public string? CoordinateEpoch { get; init; }
        public bool? Forward { get; init; }
        public int FromSrid { get; init; }
        public int ToSrid { get; init; }
        public DatumAreaEnvelope? Envelope { get; init; }
    }

    internal sealed record Resolution(DatumTransformationSelection? Selection, string? Error = null);

    private static Resolution ResolveNamedOperation(IDatumTransformationCatalog catalog, ResolutionRequest parameters)
    {
        var selection = catalog.FindByName(parameters.Value!, parameters.FromSrid, parameters.ToSrid);
        if (selection is null)
        {
            return new Resolution(null, "The named datumTransformation does not apply to the requested reprojection.");
        }

        return parameters.Forward is { } direction && direction != selection.TransformForward
            ? new Resolution(null, "transformForward does not agree with the named datumTransformation direction.")
            : new Resolution(selection);
    }

    private static Resolution ResolveOperation(IDatumTransformationCatalog catalog, ResolutionRequest parameters)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var datumTransformationValue = parameters.Value;
        var transformForwardOverride = parameters.Forward;
        var fromSrid = parameters.FromSrid;
        var toSrid = parameters.ToSrid;
        var envelope = parameters.Envelope;
        DatumTransformationSelection? selection = null;
        string? errorMessage;

        // No reprojection requested, or output equals the input SRID: nothing to do when
        // the client did not request a transformation. An explicit WKID must still be
        // parsed and validated: projected CRSs can share a geodetic base, and silently
        // accepting a transformation that does not apply to that base pair is incorrect.
        if (fromSrid == toSrid && string.IsNullOrWhiteSpace(datumTransformationValue))
        {
            return new Resolution(selection);
        }

        if (datumTransformationValue?.StartsWith("Honua:", StringComparison.Ordinal) == true)
        {
            return ResolveNamedOperation(catalog, parameters);
        }

        if (!DatumTransformationParser.TryParse(datumTransformationValue, out var request, out var parseError))
        {
            errorMessage = parseError;
            return new Resolution(null, errorMessage);
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
                return new Resolution(null, errorMessage);
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
                return new Resolution(null, errorMessage);
            }

            selection = resolved;
            return new Resolution(selection);
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

            return new Resolution(selection);
        }

        if (catalog.TryGetDefault(fromSrid, toSrid, out var defaultSelection))
        {
            selection = defaultSelection;
        }

        return new Resolution(selection);
    }
}
