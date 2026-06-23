// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Services;

/// <summary>
/// Thrown when GeoParquet (<c>f=parquet</c>) output is requested but the native
/// ParquetSharp Arrow runtime (<c>ParquetSharpNative</c>) cannot be loaded on the current
/// runtime image. The ParquetSharp NuGet package ships a glibc-only (<c>linux-x64</c>)
/// native binary that depends on <c>ld-linux-x86-64.so.2</c> / <c>libatomic.so.1</c>;
/// neither exists on the Alpine/musl runtime image (<c>linux-musl-x64</c>), so the
/// <see cref="GeoParquetFeatureWriter"/> encoder throws <see cref="DllNotFoundException"/>
/// (or a wrapping <see cref="TypeInitializationException"/>) the first time it constructs a
/// native Arrow writer (honua-server#1942).
/// </summary>
/// <remarks>
/// Protocol adapters catch this and return a clean capability response (HTTP 501) rather
/// than letting the native-load failure surface as an unhandled HTTP 500. The managed
/// GeoParquet encoding itself is spec-correct; only the runtime image is incapable of
/// loading the native encoder.
/// </remarks>
public sealed class ParquetRuntimeUnavailableException : Exception
{
    /// <summary>
    /// Human-readable capability message returned to clients when the native Parquet
    /// runtime is unavailable on the deployed image.
    /// </summary>
    public const string CapabilityMessage =
        "GeoParquet (f=parquet) output is unavailable on this runtime image: the native "
        + "ParquetSharp Arrow encoder (ParquetSharpNative) could not be loaded. The "
        + "ParquetSharp native library is glibc-only and is not supported on the Alpine/musl "
        + "image. Deploy a glibc-based runtime image to enable GeoParquet output.";

    public ParquetRuntimeUnavailableException()
        : base(CapabilityMessage)
    {
    }

    public ParquetRuntimeUnavailableException(Exception innerException)
        : base(CapabilityMessage, innerException)
    {
    }

    public ParquetRuntimeUnavailableException(string message)
        : base(message)
    {
    }

    public ParquetRuntimeUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Determines whether <paramref name="exception"/> represents a failure to load the
    /// native ParquetSharp runtime (directly, or wrapped in a
    /// <see cref="TypeInitializationException"/> raised by the first touch of a ParquetSharp
    /// type). Used by the encoder to translate the raw native-load failure into a clean
    /// <see cref="ParquetRuntimeUnavailableException"/>.
    /// </summary>
    public static bool IsNativeLoadFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException dllNotFound &&
                dllNotFound.Message.Contains("ParquetSharpNative", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // The ParquetSharp managed types run a static initializer that P/Invokes into
            // the native library; on a runtime that cannot load it the first access throws
            // TypeInitializationException wrapping the DllNotFoundException.
            if (current is TypeInitializationException typeInit &&
                typeInit.TypeName.StartsWith("ParquetSharp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
