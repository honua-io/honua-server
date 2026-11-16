// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

public sealed record MapPackageDraftRequest
{
    public string? TemplateId { get; init; }

    public IReadOnlyList<SourceBindingInput> SourceBindings { get; init; } = [];

    public string? StyleId { get; init; }

    public string? ThemeId { get; init; }

    public MapInitialViewInput? InitialView { get; init; }
}

public sealed record SourceBindingInput
{
    public string? SourceId { get; init; }

    public string? Protocol { get; init; }

    public string? Url { get; init; }

    public string? ServiceId { get; init; }

    public string? LayerId { get; init; }

    public string? Filter { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record MapInitialViewInput
{
    public IReadOnlyList<double>? Bbox { get; init; }

    public string? Crs { get; init; }
}

public sealed record MapPackageDraftResult
{
    public MapPackage? Package { get; init; }

    public IReadOnlyList<PackageDraftFinding> Errors { get; init; } = [];

    public IReadOnlyList<PackageDraftFinding> Warnings { get; init; } = [];

    public bool Succeeded => Package is not null;
}
