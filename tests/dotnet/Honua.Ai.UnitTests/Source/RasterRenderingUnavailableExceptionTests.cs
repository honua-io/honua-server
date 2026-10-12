// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

/// <summary>
/// Unit coverage for the native SkiaSharp load-failure classifier that lets the render
/// seam translate an opaque native-load failure into an actionable capability error on
/// serverless/AOT images that cannot rasterize maps (honua-server#2770).
/// </summary>
public sealed class RasterRenderingUnavailableExceptionTests
{
    [UnitTest]
    public void IsNativeLoadFailure_DllNotFoundNamingSkia_ReturnsTrue()
    {
        var ex = new DllNotFoundException(
            "Unable to load shared library 'libSkiaSharp' or one of its dependencies: "
            + "libfontconfig.so.1: cannot open shared object file");

        RasterRenderingUnavailableException.IsNativeLoadFailure(ex).Should().BeTrue();
    }

    [UnitTest]
    public void IsNativeLoadFailure_TypeInitializationWrappingSkiaDll_ReturnsTrue()
    {
        var ex = new TypeInitializationException(
            "SkiaSharp.SKImageInfo",
            new DllNotFoundException("Unable to load shared library 'libSkiaSharp'"));

        RasterRenderingUnavailableException.IsNativeLoadFailure(ex).Should().BeTrue();
    }

    [UnitTest]
    public void IsNativeLoadFailure_AggregateWrappingSkiaDll_ReturnsTrue()
    {
        var ex = new AggregateException(
            new InvalidOperationException("outer"),
            new DllNotFoundException("Unable to load shared library 'libSkiaSharp'"));

        RasterRenderingUnavailableException.IsNativeLoadFailure(ex).Should().BeTrue();
    }

    [UnitTest]
    public void IsNativeLoadFailure_BadImageFormatNamingSkia_ReturnsTrue()
    {
        var ex = new BadImageFormatException("Could not load libSkiaSharp: wrong architecture");

        RasterRenderingUnavailableException.IsNativeLoadFailure(ex).Should().BeTrue();
    }

    [UnitTest]
    public void IsNativeLoadFailure_UnrelatedDllNotFound_ReturnsFalse()
    {
        // A non-Skia native-load failure (e.g. an unrelated provider library) must not be
        // misclassified as a rendering-capability failure.
        var ex = new DllNotFoundException("Unable to load shared library 'libpq'");

        RasterRenderingUnavailableException.IsNativeLoadFailure(ex).Should().BeFalse();
    }

    [UnitTest]
    public void IsNativeLoadFailure_UnrelatedDomainException_ReturnsFalse()
    {
        RasterRenderingUnavailableException.IsNativeLoadFailure(
            new InvalidOperationException("no data in bbox")).Should().BeFalse();
    }

    [UnitTest]
    public void IsNativeLoadFailure_Null_ReturnsFalse()
    {
        RasterRenderingUnavailableException.IsNativeLoadFailure(null).Should().BeFalse();
    }

    [UnitTest]
    public void CapabilityMessage_DoesNotLeakRawNativeDetail_AndNamesRendering()
    {
        var message = new RasterRenderingUnavailableException(
            new DllNotFoundException("libfontconfig.so.1: cannot open shared object file")).Message;

        message.Should().Be(RasterRenderingUnavailableException.CapabilityMessage);
        message.Should().Contain("Map rendering is unavailable");
        message.Should().NotContain("libfontconfig.so.1");
    }
}
