// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record MapPackage
{
    public required string MapPackageId { get; init; }

    public required string Format { get; init; }

    public required PackageStatus Status { get; init; }

    public string? TemplateId { get; init; }

    public IReadOnlyList<SourceBinding> SourceBindings { get; init; } = [];

    public IReadOnlyList<StyleRef> StyleRefs { get; init; } = [];

    public string? ThemeId { get; init; }

    public JsonElement? MapSpec { get; init; }

    public MapInitialView? InitialView { get; init; }

    public IReadOnlyList<LegendEntry> Legend { get; init; } = [];

    public IReadOnlyList<PopupBinding> PopupBindings { get; init; } = [];

    public IReadOnlyList<LabelBinding> LabelBindings { get; init; } = [];

    public string? PreviewArtifactId { get; init; }

    public IReadOnlyList<string> BoundArtifacts { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public static MapPackage CreateDraft(
        string mapPackageId,
        string format,
        IReadOnlyList<SourceBinding> sourceBindings,
        IReadOnlyList<StyleRef> styleRefs,
        string? templateId = null,
        string? themeId = null)
        => new()
        {
            MapPackageId = mapPackageId,
            Format = format,
            Status = PackageStatus.Draft,
            TemplateId = templateId,
            SourceBindings = sourceBindings,
            StyleRefs = styleRefs,
            ThemeId = themeId,
            CreatedAt = DateTimeOffset.UtcNow
        };
}

public sealed record MapInitialView
{
    public required double[] Bbox { get; init; }

    public required string Crs { get; init; }
}

public sealed record PopupBinding
{
    public required string SourceId { get; init; }

    public required string FieldName { get; init; }

    public string? Template { get; init; }
}

public sealed record LabelBinding
{
    public required string SourceId { get; init; }

    public required string FieldName { get; init; }

    public string? Placement { get; init; }
}
