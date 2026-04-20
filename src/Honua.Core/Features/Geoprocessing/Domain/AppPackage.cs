// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// A composed application package produced by a geoprocessing workflow.
/// </summary>
public sealed record AppPackage
{
    /// <summary>
    /// Unique identifier for this application package.
    /// </summary>
    public required string AppPackageId { get; init; }

    /// <summary>
    /// Target SDK for the generated application (e.g. "honua-sdk-js").
    /// </summary>
    public required string TargetSdk { get; init; }

    /// <summary>
    /// Package format identifier (e.g. "honua_app_package.v1").
    /// </summary>
    public required string Format { get; init; }

    /// <summary>
    /// Current lifecycle status of the package.
    /// </summary>
    public required PackageStatus Status { get; init; }

    /// <summary>
    /// Optional reference to a MapTemplate metadata resource.
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>
    /// Entry point file for the generated application.
    /// </summary>
    public string? EntryPoint { get; init; }

    /// <summary>
    /// Paths of files generated for the application.
    /// </summary>
    public IReadOnlyList<string> GeneratedFiles { get; init; } = [];

    /// <summary>
    /// Artifact identifier for the bundled tarball.
    /// </summary>
    public string? BundleArtifactId { get; init; }

    /// <summary>
    /// Manifest of static assets included in the package.
    /// </summary>
    public IReadOnlyList<AssetManifestEntry> AssetManifest { get; init; } = [];

    /// <summary>
    /// Optional reference to a composed map package used by the application.
    /// </summary>
    public string? MapPackageId { get; init; }

    /// <summary>
    /// JSON Schema describing runtime configuration options.
    /// </summary>
    public JsonElement? RuntimeConfigSchema { get; init; }

    /// <summary>
    /// Hints for downstream deployment and delivery.
    /// </summary>
    public DeliveryHints? DeliveryHints { get; init; }

    /// <summary>
    /// Artifact identifiers bound to this package.
    /// </summary>
    public IReadOnlyList<string> BoundArtifacts { get; init; } = [];

    /// <summary>
    /// Timestamp when the package was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp of the last status change.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Creates a new draft application package.
    /// </summary>
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

/// <summary>
/// An entry in the application asset manifest.
/// </summary>
public sealed record AssetManifestEntry
{
    /// <summary>
    /// Relative path of the asset within the package.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// MIME content type of the asset.
    /// </summary>
    public required string ContentType { get; init; }
}

/// <summary>
/// Deployment and delivery hints for an application package.
/// </summary>
public sealed record DeliveryHints
{
    /// <summary>
    /// Hosting mode (e.g. "static_site", "embedded").
    /// </summary>
    public string? HostingMode { get; init; }

    /// <summary>
    /// Default route prefix for the hosted application.
    /// </summary>
    public string? DefaultRoutePrefix { get; init; }
}
