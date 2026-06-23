// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Services;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Unit coverage for the native-load-failure detector that lets the GeoParquet writer
/// translate a missing/incompatible ParquetSharpNative runtime into a clean capability
/// response (HTTP 501) instead of an unhandled 500 (honua-server#1942).
/// </summary>
public sealed class ParquetRuntimeUnavailableExceptionTests
{
    [Fact]
    public void IsNativeLoadFailure_DirectDllNotFoundForParquetSharpNative_ReturnsTrue()
    {
        var exception = new DllNotFoundException(
            "Unable to load shared library 'ParquetSharpNative' or one of its dependencies.");

        ParquetRuntimeUnavailableException.IsNativeLoadFailure(exception).Should().BeTrue();
    }

    [Fact]
    public void IsNativeLoadFailure_WrappedInTypeInitialization_ReturnsTrue()
    {
        // The first touch of a ParquetSharp type runs a static initializer that P/Invokes
        // into the native library; on a musl runtime that cannot load it, the failure
        // surfaces as a TypeInitializationException wrapping the DllNotFoundException.
        var inner = new DllNotFoundException(
            "Error loading shared library ld-linux-x86-64.so.2: needed by /app/ParquetSharpNative.so");
        var exception = new TypeInitializationException("ParquetSharp.Arrow.ArrowWriterPropertiesBuilder", inner);

        ParquetRuntimeUnavailableException.IsNativeLoadFailure(exception).Should().BeTrue();
    }

    [Fact]
    public void IsNativeLoadFailure_UnrelatedDllNotFound_ReturnsFalse()
    {
        var exception = new DllNotFoundException("Unable to load shared library 'some_other_native'.");

        ParquetRuntimeUnavailableException.IsNativeLoadFailure(exception).Should().BeFalse();
    }

    [Fact]
    public void IsNativeLoadFailure_UnrelatedException_ReturnsFalse()
    {
        ParquetRuntimeUnavailableException.IsNativeLoadFailure(new InvalidOperationException("boom"))
            .Should().BeFalse();
        ParquetRuntimeUnavailableException.IsNativeLoadFailure(null).Should().BeFalse();
    }

    [Fact]
    public void Exception_CarriesCapabilityMessageAndInner()
    {
        var inner = new DllNotFoundException("ParquetSharpNative missing");
        var exception = new ParquetRuntimeUnavailableException(inner);

        exception.Message.Should().Be(ParquetRuntimeUnavailableException.CapabilityMessage);
        exception.Message.Should().Contain("f=parquet");
        exception.InnerException.Should().BeSameAs(inner);
    }
}
