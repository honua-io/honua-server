// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

public sealed record AppDraftRequest
{
    public string? TemplateId { get; init; }

    public string? TargetSdk { get; init; }

    public string? MapId { get; init; }

    public IReadOnlyList<string> BoundArtifactIds { get; init; } = [];

    public JsonElement? RuntimeConfig { get; init; }
}

public sealed record AppDraftResult
{
    public App? App { get; init; }

    public IReadOnlyList<PackageDraftFinding> Errors { get; init; } = [];

    public IReadOnlyList<PackageDraftFinding> Warnings { get; init; } = [];

    public bool Succeeded => App is not null;
}
