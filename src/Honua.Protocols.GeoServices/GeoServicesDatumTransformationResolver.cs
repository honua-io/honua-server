// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Protocols.GeoServices.FeatureServer.Services;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Shared resolution of the Esri GeoServices <c>datumTransformation</c> parameter into
/// an authoritative <see cref="DatumTransformationSelection"/> drawn from the
/// <see cref="IDatumTransformationCatalog"/>. Centralizing this keeps the FeatureServer
/// query path, the Geometry Service <c>project</c> operation, and the ImageServer
/// <c>project</c> operation on one WKID → PROJ pipeline mapping rather than each
/// reimplementing transformation selection.
/// </summary>
internal static class GeoServicesDatumTransformationResolver
{
    /// <summary>
    /// Resolves the datum transformation for a <paramref name="fromSrid"/> →
    /// <paramref name="toSrid"/> reprojection. Honors a client-supplied
    /// <c>datumTransformation</c> WKID/composite when present; otherwise applies the
    /// catalog's Esri default for the SRID pair. An unknown or inapplicable client
    /// request yields an explicit Esri-style error message rather than a silent
    /// substitute.
    /// </summary>
    /// <param name="catalog">The datum-transformation catalog (WKID → PROJ pipeline).</param>
    /// <param name="datumTransformationValue">
    /// The raw <c>datumTransformation</c> parameter value (may be <see langword="null"/>/empty).
    /// </param>
    /// <param name="fromSrid">Source SRID (input geometry's spatial reference).</param>
    /// <param name="toSrid">Target SRID (requested output spatial reference).</param>
    /// <param name="selection">
    /// On success, the resolved transformation, or <see langword="null"/> when no curated
    /// default exists (callers then let PROJ pick its default 2-argument pipeline, which is
    /// correct for identity/no-shift pairs).
    /// </param>
    /// <param name="errorMessage">
    /// On failure, an Esri-style error message describing why the request was rejected.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when resolution succeeds (including the no-transformation
    /// case); <see langword="false"/> with <paramref name="errorMessage"/> set when the
    /// client request is malformed or unsupported for the pair.
    /// </returns>
    public static bool TryResolve(
        IDatumTransformationCatalog catalog,
        string? datumTransformationValue,
        int fromSrid,
        int toSrid,
        out DatumTransformationSelection? selection,
        [NotNullWhen(false)] out string? errorMessage)
        => TryResolveWithDirection(
            catalog,
            datumTransformationValue,
            transformForwardOverride: null,
            fromSrid,
            toSrid,
            out selection,
            out errorMessage);

    /// <summary>
    /// Resolves a transformation with an optional Geometry Service top-level
    /// <c>transformForward</c> override.
    /// </summary>
    public static bool TryResolveWithDirection(
        IDatumTransformationCatalog catalog,
        string? datumTransformationValue,
        bool? transformForwardOverride,
        int fromSrid,
        int toSrid,
        out DatumTransformationSelection? selection,
        [NotNullWhen(false)] out string? errorMessage)
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

        // No client choice: apply the Esri default for the pair when one exists.
        // When no curated default exists, leave selection null so PROJ uses its default
        // pipeline (correct for identity/no-shift pairs).
        if (catalog.TryGetDefault(fromSrid, toSrid, out var defaultSelection))
        {
            selection = defaultSelection;
        }

        return true;
    }
}
