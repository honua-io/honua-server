// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

// Raster storage is optional: a feature-only install has no raster tables. Writes that need
// them raise this so callers answer with an operator action instead of an internal error.
public sealed class RasterStorageUnavailableException : Exception
{
    public const string CapabilityMessage =
        "Raster storage isn't installed on this server: the database has no postgis_raster extension. " +
        "Enable postgis_raster and restart the server to import rasters.";

    public RasterStorageUnavailableException()
        : base(CapabilityMessage)
    {
    }

    public RasterStorageUnavailableException(string message)
        : base(message)
    {
    }

    public RasterStorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
