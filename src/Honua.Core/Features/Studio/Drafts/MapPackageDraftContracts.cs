// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>
/// Structured, natural-language-free input for a deterministic
/// <see cref="MapPackage"/> draft (ADR-0076).
/// </summary>
/// <remarks>
/// The members mirror the geospatial-mcp <c>create_map_package</c> composition
/// selectors. There is deliberately no prompt member: draft creation is a pure
/// projection of structured input, and model inference belongs to the client.
/// Members are permissive wire-shaped values rather than domain types so that
/// every validation rule lives in one place (the factory) instead of being
/// split across each protocol adapter.
/// </remarks>
public sealed record MapPackageDraftRequest
{
    /// <summary>
    /// Registry-defined MapTemplate identifier seeding the composition.
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>
    /// Protocol-aware bindings of map layers to data sources.
    /// </summary>
    public IReadOnlyList<SourceBindingInput> SourceBindings { get; init; } = [];

    /// <summary>
    /// StyleRef selection applied to the composition.
    /// </summary>
    public string? StyleId { get; init; }

    /// <summary>
    /// ThemeSpec selection applied to the composition.
    /// </summary>
    public string? ThemeId { get; init; }

    /// <summary>
    /// Initial viewport for the composed map.
    /// </summary>
    public MapInitialViewInput? InitialView { get; init; }
}

/// <summary>
/// Wire-shaped source binding input validated and mapped onto the canonical
/// <see cref="SourceBinding"/> by <see cref="IMapPackageDraftFactory"/>.
/// </summary>
public sealed record SourceBindingInput
{
    /// <summary>
    /// Identifier for this source within the package.
    /// </summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// Wire name of the source protocol, for example <c>ogc_features</c>.
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Base URL of the source service. Optional: an unresolved locator is a
    /// deferred warning rather than a blocking error.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Optional service identifier within the endpoint.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Optional layer identifier within the service.
    /// </summary>
    public string? LayerId { get; init; }

    /// <summary>
    /// Optional server-side filter expression applied to the source.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Opaque metadata associated with the source binding.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Wire-shaped initial viewport input validated and mapped onto the canonical
/// <see cref="MapInitialView"/>.
/// </summary>
public sealed record MapInitialViewInput
{
    /// <summary>
    /// Bounding box as <c>[minLon, minLat, maxLon, maxLat]</c>.
    /// </summary>
    public IReadOnlyList<double>? Bbox { get; init; }

    /// <summary>
    /// Coordinate reference system. Defaults to
    /// <see cref="MapPackageDraftFactory.DefaultCrs"/> when omitted.
    /// </summary>
    public string? Crs { get; init; }
}

/// <summary>
/// Outcome of a deterministic map draft creation: either a package or the
/// structural errors that prevented one.
/// </summary>
public sealed record MapPackageDraftResult
{
    /// <summary>
    /// The created draft, or <see langword="null"/> when
    /// <see cref="Errors"/> is non-empty.
    /// </summary>
    public MapPackage? Package { get; init; }

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
/// Creates <see cref="MapPackage"/> drafts deterministically from structured
/// input, with no model inference and no natural-language input (ADR-0076).
/// </summary>
public interface IMapPackageDraftFactory
{
    /// <summary>
    /// Validates <paramref name="request"/> and projects it onto a draft
    /// <see cref="MapPackage"/>.
    /// </summary>
    /// <param name="request">The structured composition input.</param>
    /// <returns>
    /// A result carrying the created package, or the blocking findings that
    /// prevented creation.
    /// </returns>
    MapPackageDraftResult CreateDraft(MapPackageDraftRequest request);
}
