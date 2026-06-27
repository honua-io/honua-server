// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Domain;

/// <summary>
/// Versioned snapshot of every operation currently available in the Honua Operations
/// Toolset. Mirrors the workflow node registry snapshot one level up.
/// </summary>
public sealed record OperationCatalogSnapshot
{
    /// <summary>
    /// Stable version fingerprint for this catalog snapshot.
    /// </summary>
    public required string CatalogVersion { get; init; }

    /// <summary>
    /// UTC timestamp when the snapshot was generated.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Provider snapshots that contributed operations or warnings.
    /// </summary>
    public required IReadOnlyList<OperationProviderSnapshot> Providers { get; init; }

    /// <summary>
    /// Operations available for deterministic or AI-orchestrated invocation.
    /// </summary>
    public required IReadOnlyList<HonuaOperationDefinition> Operations { get; init; }

    /// <summary>
    /// Non-fatal catalog-level warnings.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Describes a single operation provider that contributed to a catalog snapshot.
/// </summary>
public sealed record OperationProviderSnapshot
{
    /// <summary>
    /// Stable provider identifier.
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Whether the provider produced operations without failing.
    /// </summary>
    public bool Available { get; init; } = true;

    /// <summary>
    /// Provider warnings that should be surfaced by clients.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
