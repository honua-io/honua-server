// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

// Style hints are recorded apart from the pixels, because lookup tables and NoData markers are not copied with the raster.
public sealed record MigrationCoverageStyleDiagnostic
{
    public required string Kind { get; init; }

    public required string Classification { get; init; }

    public required string SourceCoverageId { get; init; }

    public string? SourceStyleId { get; init; }

    public required string Reason { get; init; }

    public string? SuggestedTargetStyleId { get; init; }

    public string? VendorName { get; init; }

    public string[] ManualSteps { get; init; } = [];
}
