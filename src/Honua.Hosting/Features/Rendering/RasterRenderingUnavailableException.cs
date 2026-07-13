// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Thrown when server-side map rendering is requested but the native SkiaSharp
/// rasterization runtime (<c>libSkiaSharp</c>) cannot be initialized on the current
/// runtime image. The <c>SkiaSharp.NativeAssets.Linux</c> package ships
/// <c>libSkiaSharp.so</c> dynamically linked against <c>libfontconfig.so.1</c> /
/// <c>libfreetype.so.6</c>; on a minimal serverless/AOT container that omits those
/// system libraries (or ships the wrong-architecture native asset) the first
/// P/Invoke into Skia — allocating the render surface — throws
/// <see cref="DllNotFoundException"/> (often wrapped in a
/// <see cref="TypeInitializationException"/>), which would otherwise surface to
/// callers as an opaque, generic internal error (honua-server#2770).
/// </summary>
/// <remarks>
/// Protocol adapters catch this and return a clean, actionable capability response
/// (the MCP <c>honua_render_map</c> tool maps it to a <c>failed_precondition</c>
/// envelope) rather than letting the native-load failure surface as a generic
/// <c>internal</c> / <c>ExecutionFailed</c> error. The managed rendering pipeline
/// itself is correct; only the deployed runtime image is incapable of loading the
/// native rasterizer. Mirrors <see cref="Honua.Infrastructure.Services.ParquetRuntimeUnavailableException"/>.
/// </remarks>
public sealed class RasterRenderingUnavailableException : Exception
{
    /// <summary>
    /// Human-readable capability message returned to clients when the native Skia
    /// rendering runtime is unavailable on the deployed image.
    /// </summary>
    public const string CapabilityMessage =
        "Map rendering is unavailable on this runtime image: the native SkiaSharp "
        + "rasterization library (libSkiaSharp) could not be loaded. This typically means "
        + "the serverless/AOT container is missing the native rendering library or its "
        + "system dependencies (libfontconfig, libfreetype). Non-rendering tools such as "
        + "feature queries remain available. Deploy a runtime image that installs the "
        + "SkiaSharp native dependencies to enable map rendering.";

    /// <summary>
    /// Initializes a new instance with the default capability message.
    /// </summary>
    public RasterRenderingUnavailableException()
        : base(CapabilityMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance that wraps the underlying native-load failure so
    /// the real cause is preserved for server-side diagnostics.
    /// </summary>
    /// <param name="innerException">The raw native-load failure.</param>
    public RasterRenderingUnavailableException(Exception innerException)
        : base(CapabilityMessage, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom message.
    /// </summary>
    /// <param name="message">The capability message.</param>
    public RasterRenderingUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom message and inner exception.
    /// </summary>
    /// <param name="message">The capability message.</param>
    /// <param name="innerException">The raw native-load failure.</param>
    public RasterRenderingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Determines whether <paramref name="exception"/> represents a failure to load the
    /// native SkiaSharp rendering runtime (directly, or wrapped in a
    /// <see cref="TypeInitializationException"/> raised by the first touch of a SkiaSharp
    /// type, or aggregated). Used by the render seam to translate the raw native-load
    /// failure into a clean <see cref="RasterRenderingUnavailableException"/>.
    /// </summary>
    /// <param name="exception">The exception to inspect.</param>
    /// <returns><see langword="true"/> when the exception chain indicates a Skia native-load failure.</returns>
    public static bool IsNativeLoadFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (IsNativeLoadFailure(inner))
                    {
                        return true;
                    }
                }
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
