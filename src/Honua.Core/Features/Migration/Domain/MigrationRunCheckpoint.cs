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

public sealed record MigrationRunCheckpoint
{
    public required string RunId { get; init; }

    public required string Phase { get; init; }

    // On write, a marker containing :// is replaced wholesale and then cut to the marker length bound.
    public required string ResumeMarker { get; init; }

    public int CompletedItemCount { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    public int Attempt { get; init; } = 1;
}
