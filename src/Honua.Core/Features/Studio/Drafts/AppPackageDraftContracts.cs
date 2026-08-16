// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>
/// Structured, natural-language-free input for a deterministic
/// <see cref="AppPackage"/> draft (ADR-0076).
/// </summary>
/// <remarks>
/// The members mirror the geospatial-mcp <c>create_app_package</c> fields. As
/// with <see cref="MapPackageDraftRequest"/> there is deliberately no prompt
/// member.
/// </remarks>
public sealed record AppPackageDraftRequest
{
    /// <summary>
    /// App-template registry identifier surfaced through
    /// <see cref="AppPackage.TemplateId"/>.
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>
    /// Target SDK declaration. Defaults to
    /// <see cref="AppPackageDraftFactory.DefaultTargetSdk"/> when omitted.
    /// </summary>
    public string? TargetSdk { get; init; }

    /// <summary>
    /// Identifier of the <see cref="MapPackage"/> bound into the application.
    /// </summary>
    public string? MapPackageId { get; init; }

    /// <summary>
    /// ArtifactRef identifiers required at runtime.
    /// </summary>
    public IReadOnlyList<string> BoundArtifactIds { get; init; } = [];

    /// <summary>
    /// Runtime configuration values projected into the application. Must be a
    /// JSON object when present.
    /// </summary>
    public JsonElement? RuntimeConfig { get; init; }
}

/// <summary>
/// Outcome of a deterministic app draft creation: either a package or the
/// structural errors that prevented one.
/// </summary>
public sealed record AppPackageDraftResult
{
    /// <summary>
    /// The created draft, or <see langword="null"/> when
    /// <see cref="Errors"/> is non-empty.
    /// </summary>
    public AppPackage? Package { get; init; }

    /// <summary>
    /// Blocking structural findings. A non-empty list means no package was created.
    /// </summary>
    public IReadOnlyList<PackageDraftFinding> Errors { get; init; } = [];

    /// <summary>
    /// Non-blocking findings deferred to publish-time resolution.
    /// </summary>
    public IReadOnlyList<PackageDraftFinding> Warnings { get; init; } = [];

    /// <summary>
    /// Whether a package was created.
    /// </summary>
    public bool Succeeded => Package is not null;
}

/// <summary>
/// Creates <see cref="AppPackage"/> drafts deterministically from structured
/// input, with no model inference and no natural-language input (ADR-0076).
/// </summary>
public interface IAppPackageDraftFactory
{
    /// <summary>
    /// Validates <paramref name="request"/> and projects it onto a draft
    /// <see cref="AppPackage"/>.
    /// </summary>
    /// <param name="request">The structured composition input.</param>
    /// <returns>
    /// A result carrying the created package, or the blocking findings that
    /// prevented creation.
    /// </returns>
    AppPackageDraftResult CreateDraft(AppPackageDraftRequest request);
}
