// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Stable metadata keys projected onto result-package artifacts built from typed
/// raster output descriptors (#3089). Protocol adapters use these to emit the
/// authenticated content route for staged artifacts and to surface content-identity
/// metadata; values are stable identities only — never credentials or expiring URLs.
/// </summary>
public static class RasterOutputArtifactMetadata
{
    /// <summary>"true" when the artifact is a staged object served through the content route.</summary>
    public const string Staged = "raster.output.staged";

    /// <summary>
    /// "true" when the durable reference is descriptor-shaped but this release cannot
    /// safely interpret or validate it (for example a future contract version or an
    /// invalid content identity). The artifact is surfaced as unavailable rather than
    /// leaking the raw descriptor JSON to clients.
    /// </summary>
    public const string Unsupported = "raster.output.unsupported";

    /// <summary>
    /// Host-relative authenticated route for downloading a staged artifact. This is
    /// protocol-neutral and never contains a provider location or credential.
    /// </summary>
    public const string ContentRoute = "raster.output.contentRoute";

    /// <summary>Producing attempt number.</summary>
    public const string Attempt = "raster.output.attempt";

    /// <summary>Logical output name recorded by the producer.</summary>
    public const string OutputName = "raster.output.name";

    /// <summary>Exact content size in bytes.</summary>
    public const string SizeBytes = "raster.output.sizeBytes";

    /// <summary>Content checksum as <c>{algorithm}:{hex}</c>.</summary>
    public const string Checksum = "raster.output.checksum";

    /// <summary>Bare IANA media type.</summary>
    public const string MediaType = "raster.output.mediaType";

    /// <summary>Producing engine identity.</summary>
    public const string ProducingEngine = "raster.output.producingEngine";

    /// <summary>Storage provider of a staged object.</summary>
    public const string StoreProvider = "raster.output.store.provider";

    /// <summary>Logical store reference of a staged object.</summary>
    public const string StoreReference = "raster.output.store.reference";

    /// <summary>Attempt-scoped object key of a staged object.</summary>
    public const string ObjectKey = "raster.output.store.objectKey";

    /// <summary>Output grid width in pixels.</summary>
    public const string GridWidth = "raster.output.grid.width";

    /// <summary>Output grid height in pixels.</summary>
    public const string GridHeight = "raster.output.grid.height";

    /// <summary>Output band count.</summary>
    public const string GridBandCount = "raster.output.grid.bandCount";

    /// <summary>Largest sample width across bands, in bits.</summary>
    public const string GridBitsPerSample = "raster.output.grid.bitsPerSample";

    /// <summary>Coordinate reference system identifier when pinned by the producer.</summary>
    public const string GridCrs = "raster.output.grid.crs";

    /// <summary>Catalog process identifier lineage.</summary>
    public const string LineageProcessId = "raster.output.lineage.processId";

    /// <summary>Analysis plan identifier lineage.</summary>
    public const string LineagePlanId = "raster.output.lineage.planId";

    /// <summary>Pipe-separated stable source references consumed by the producer.</summary>
    public const string LineageSources = "raster.output.lineage.sources";

    /// <summary>COG catalog raster id recorded after successful registration.</summary>
    public const string RegisteredCatalogRasterId = "raster.output.registered.cogCatalogRasterId";

    /// <summary>Catalog layer id the output registered into.</summary>
    public const string RegisteredLayerId = "raster.output.registered.layerId";
}
