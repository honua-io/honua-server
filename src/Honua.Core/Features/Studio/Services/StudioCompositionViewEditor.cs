// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Services;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

public static class StudioCompositionViewEditor
{
    private sealed record CameraBound(
        string Member,
        Func<StudioCompositionView, double?> Read,
        Func<double, bool> Contains,
        string Interval);

    private static readonly CameraBound[] CameraBounds =
    [
        new("zoom", view => view.Zoom, StudioCompositionViewBounds.IsZoomInBounds,
            Closed(StudioCompositionViewBounds.MinZoom, StudioCompositionViewBounds.MaxZoom)),
        new("pitch", view => view.Pitch, StudioCompositionViewBounds.IsPitchInBounds,
            Closed(StudioCompositionViewBounds.MinPitch, StudioCompositionViewBounds.MaxPitch)),
        new("bearing", view => view.Bearing, StudioCompositionViewBounds.IsBearingInBounds,
            HalfOpen(StudioCompositionViewBounds.MinBearing, StudioCompositionViewBounds.MaxBearingExclusive)),
    ];

    public static StudioCompositionBody MergeView(StudioCompositionBody body, StudioCompositionView patch)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(patch);
        EnsureCameraWithinBounds(patch);

        var current = body.View ?? new StudioCompositionView();
        return body with
        {
            View = new StudioCompositionView
            {
                Bbox = patch.Bbox ?? current.Bbox,
                Center = patch.Center ?? current.Center,
                Zoom = patch.Zoom ?? current.Zoom,
                Pitch = patch.Pitch ?? current.Pitch,
                Bearing = patch.Bearing ?? current.Bearing,
                Crs = patch.Crs ?? current.Crs,
            },
        };
    }

    private static void EnsureCameraWithinBounds(StudioCompositionView patch)
    {
        foreach (var bound in CameraBounds)
        {
            if (bound.Read(patch) is { } value && !bound.Contains(value))
            {
                throw new StudioCompositionOutOfBoundsException(
                    $"setView: {bound.Member} {EcmaScriptNumberFormatter.Format(value)} is out of bounds {bound.Interval}");
            }
        }
    }

    private static string Closed(double min, double max) =>
        $"[{EcmaScriptNumberFormatter.Format(min)}, {EcmaScriptNumberFormatter.Format(max)}]";

    private static string HalfOpen(double min, double maxExclusive) =>
        $"[{EcmaScriptNumberFormatter.Format(min)}, {EcmaScriptNumberFormatter.Format(maxExclusive)})";
}

public sealed class StudioCompositionOutOfBoundsException : Exception
{
    public const string ErrorCode = "out-of-bounds";

    public StudioCompositionOutOfBoundsException(string message) : base(message)
    {
    }
}
