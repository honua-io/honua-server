// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record AppPackage
{
    public required string AppPackageId { get; init; }

    public required string TargetSdk { get; init; }

    public required string Format { get; init; }

    public required PackageStatus Status { get; init; }

    public string? TemplateId { get; init; }

    public string? EntryPoint { get; init; }

    public IReadOnlyList<string> GeneratedFiles { get; init; } = [];

    public string? BundleArtifactId { get; init; }

    public string? ManifestArtifactId { get; init; }

    public IReadOnlyList<AssetManifestEntry> AssetManifest { get; init; } = [];

    public string? MapPackageId { get; init; }

    public JsonElement? RuntimeConfigSchema { get; init; }

    public JsonElement? RuntimeConfig { get; init; }

    public AppSharePolicy? SharePolicy { get; init; }

    public DeliveryHints? DeliveryHints { get; init; }

    public IReadOnlyList<string> BoundArtifacts { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public static AppPackage CreateDraft(
        string appPackageId,
        string targetSdk,
        string format,
        string? templateId = null,
        string? mapPackageId = null)
        => new()
        {
            AppPackageId = appPackageId,
            TargetSdk = targetSdk,
            Format = format,
            Status = PackageStatus.Draft,
            TemplateId = templateId,
            MapPackageId = mapPackageId,
            CreatedAt = DateTimeOffset.UtcNow
        };
}

public sealed record AppSharePolicy
{
    public required string Visibility { get; init; }

    public required bool Embed { get; init; }

    public required bool Reviewed { get; init; }

    public static AppSharePolicy Closed { get; } = new()
    {
        Visibility = "private",
        Embed = false,
        Reviewed = false
    };
}

public sealed record AssetManifestEntry
{
    public required string Path { get; init; }

    public required string ContentType { get; init; }
}

public sealed record DeliveryHints
{
    public string? HostingMode { get; init; }

    public string? DefaultRoutePrefix { get; init; }
}
