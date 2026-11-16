// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Raster;

public static class RasterOutputArtifactMetadata
{
    public const string Staged = "raster.output.staged";

    public const string Unsupported = "raster.output.unsupported";

    public const string ContentRoute = "raster.output.contentRoute";

    public const string Attempt = "raster.output.attempt";

    public const string OutputName = "raster.output.name";

    public const string SizeBytes = "raster.output.sizeBytes";

    public const string Checksum = "raster.output.checksum";

    public const string MediaType = "raster.output.mediaType";

    public const string ProducingEngine = "raster.output.producingEngine";

    public const string StoreProvider = "raster.output.store.provider";

    public const string StoreReference = "raster.output.store.reference";

    public const string ObjectKey = "raster.output.store.objectKey";

    public const string GridWidth = "raster.output.grid.width";

    public const string GridHeight = "raster.output.grid.height";

    public const string GridBandCount = "raster.output.grid.bandCount";

    public const string GridBitsPerSample = "raster.output.grid.bitsPerSample";

    public const string GridPixelScaleX = "raster.output.grid.pixelScaleX";

    public const string GridPixelScaleY = "raster.output.grid.pixelScaleY";

    public const string GridCrs = "raster.output.grid.crs";

    public const string LineageProcessId = "raster.output.lineage.processId";

    public const string LineagePlanId = "raster.output.lineage.planId";

    public const string LineageSources = "raster.output.lineage.sources";

    public const string RegisteredCatalogRasterId = "raster.output.registered.cogCatalogRasterId";

    public const string RegisteredLayerId = "raster.output.registered.layerId";
}
