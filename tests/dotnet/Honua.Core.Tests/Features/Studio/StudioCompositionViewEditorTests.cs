// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

public sealed class StudioCompositionViewEditorTests
{
    private static readonly StudioCompositionBody Framed = StudioCompositionBody.Empty with
    {
        View = new StudioCompositionView { Center = [-157.86, 21.3], Zoom = 9, Bearing = 10, Crs = "EPSG:4326" },
    };

    [UnitTest]
    public void MergeView_WithAPartialView_KeepsTheMembersItLeavesUnset()
    {
        var merged = StudioCompositionViewEditor.MergeView(Framed, new StudioCompositionView { Zoom = 12, Pitch = 30 });

        Assert.Equal(
            new StudioCompositionView { Center = Framed.View!.Center, Zoom = 12, Pitch = 30, Bearing = 10, Crs = "EPSG:4326" },
            merged.View);
    }

    [UnitTest]
    public void MergeView_WhenTheBodyHasNoView_StoresThePatch()
    {
        var merged = StudioCompositionViewEditor.MergeView(StudioCompositionBody.Empty, new StudioCompositionView { Zoom = 3 });

        Assert.Equal(new StudioCompositionView { Zoom = 3 }, merged.View);
    }

    public static TheoryData<StudioCompositionView, string> OutOfBoundsCameraPatches => new()
    {
        { new StudioCompositionView { Zoom = 30 }, "setView: zoom 30 is out of bounds [0, 24]" },
        { new StudioCompositionView { Zoom = -0.5 }, "setView: zoom -0.5 is out of bounds [0, 24]" },
        { new StudioCompositionView { Pitch = 85.5 }, "setView: pitch 85.5 is out of bounds [0, 85]" },
        { new StudioCompositionView { Bearing = 360 }, "setView: bearing 360 is out of bounds [0, 360)" },
        { new StudioCompositionView { Bearing = -1 }, "setView: bearing -1 is out of bounds [0, 360)" },
    };

    [Trait("Tier", "Fast")]
    [Theory]
    [MemberData(nameof(OutOfBoundsCameraPatches))]
    public void MergeView_WithACameraMemberOutOfBounds_RefusesWithTheReducerMessage(StudioCompositionView patch, string message)
    {
        var thrown = Assert.Throws<StudioCompositionOutOfBoundsException>(
            () => StudioCompositionViewEditor.MergeView(Framed, patch));

        Assert.Equal(message, thrown.Message);
    }

    [UnitTest]
    public void MergeView_AtTheInclusiveBounds_IsAccepted()
    {
        var merged = StudioCompositionViewEditor.MergeView(
            Framed, new StudioCompositionView { Zoom = 24, Pitch = 85, Bearing = 359.99 });

        Assert.Equal(24, merged.View!.Zoom);
    }
}
