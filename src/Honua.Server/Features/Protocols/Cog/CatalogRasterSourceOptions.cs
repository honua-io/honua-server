// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Protocols.Cog;

internal sealed class CatalogRasterSourceOptions
{
    public const string SectionName = "Cog:RasterSource";

    [Range(1024L * 1024L, 64L * 1024L * 1024L * 1024L,
        ErrorMessage = "MaxDecodedRasterBytes must be between 1 MiB and 64 GiB")]
    public long MaxDecodedRasterBytes { get; set; } = 2L * 1024L * 1024L * 1024L;
}
