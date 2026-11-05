// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Services;

// The managed encoding is fine; this means the image cannot load the native encoder, and adapters should answer 501 instead of 500.
public sealed class ParquetRuntimeUnavailableException : Exception
{
    public const string CapabilityMessage =
        "GeoParquet (f=parquet) output is unavailable on this runtime image: the native "
        + "ParquetSharp Arrow encoder (ParquetSharpNative) could not be loaded. The "
        + "ParquetSharp native library requires glibc and its native dependencies, including "
        + "libatomic.so.1. Deploy a compatible runtime image to enable GeoParquet output.";

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
