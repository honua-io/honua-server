// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Rendering;

// The managed pipeline is fine; this means the runtime image cannot load the native rasterizer, and adapters should say so instead of returning a generic failure.
public sealed class RasterRenderingUnavailableException : Exception
{
    public const string CapabilityMessage =
        "Map rendering is unavailable on this runtime image: the native SkiaSharp "
        + "rasterization library (libSkiaSharp) could not be loaded. This typically means "
        + "the serverless/AOT container is missing the native rendering library or its "
        + "system dependencies (libfontconfig, libfreetype). Non-rendering tools such as "
        + "feature queries remain available. Deploy a runtime image that installs the "
        + "SkiaSharp native dependencies to enable map rendering.";

    public RasterRenderingUnavailableException()
        : base(CapabilityMessage)
    {
    }

    public RasterRenderingUnavailableException(Exception innerException)
        : base(CapabilityMessage, innerException)
    {
    }

    public RasterRenderingUnavailableException(string message)
        : base(message)
    {
    }

    public RasterRenderingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static bool IsNativeLoadFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(IsNativeLoadFailure))
            {
                return true;
            }

            // A missing/incompatible native library surfaces as one of these when the
            // managed SkiaSharp interop layer performs its first P/Invoke.
            if (current is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException &&
                ReferencesSkia(current.Message))
            {
                return true;
            }

            // SkiaSharp managed types run a static initializer that P/Invokes into the
            // native library; on a runtime that cannot load it the first access throws
            // TypeInitializationException wrapping the underlying native-load failure.
            if (current is TypeInitializationException typeInit &&
                typeInit.TypeName.StartsWith("SkiaSharp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ReferencesSkia(string message) =>
        message.Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("libSkiaSharp", StringComparison.OrdinalIgnoreCase);
}
