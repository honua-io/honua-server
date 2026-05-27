// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Lightweight snapshot of a catalog that the spec validator consults to
/// resolve <c>@</c>-references against external services/resources. Kept
/// deliberately narrow so that external tools (CLIs, CI lint) can supply a
/// static fixture rather than the full Metadata v2 graph runtime.
/// </summary>
public interface ISpecCatalogSnapshot
{
    /// <summary>
    /// Opaque version string used as a cache key. Must change whenever any
    /// catalog entry changes so that <see cref="Validation.ReferenceResolver"/>
    /// correctly invalidates its per-snapshot cache.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Resolves a <c>sources[*]</c> binding against the external catalog.
    /// The implementation typically consults the Metadata v2 graph for
    /// resource refs or a STAC/COG/Parquet lookup for others.
    /// </summary>
    /// <param name="source">Parsed source binding from the spec.</param>
    /// <returns>Resolved type (with catalog-supplied columns/bands) or <c>null</c> when the target cannot be found.</returns>
    TypeRef? ResolveSource(SourceBinding source);
}

/// <summary>
/// Static in-memory catalog snapshot used for offline validation. Each entry
/// is a <c>(source id, resolved type)</c> tuple — the snapshot returns the
/// type when the source id matches, regardless of the source's declared ref.
/// </summary>
public sealed class StaticSpecCatalogSnapshot : ISpecCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, TypeRef> _sources;

    /// <summary>
    /// Creates a new static snapshot.
    /// </summary>
    /// <param name="version">Version string (used as cache key).</param>
    /// <param name="sources">Resolved type per source id.</param>
    public StaticSpecCatalogSnapshot(string version, IReadOnlyDictionary<string, TypeRef> sources)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    /// <summary>
    /// Empty snapshot used as the default when no catalog is available. The
    /// resolver degrades to structural-only validation and emits a
    /// <see cref="SpecDiagnosticCode.CatalogUnavailable"/> warning per source.
    /// </summary>
    public static ISpecCatalogSnapshot Empty { get; } = new StaticSpecCatalogSnapshot("empty", new Dictionary<string, TypeRef>(StringComparer.Ordinal));

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public TypeRef? ResolveSource(SourceBinding source)
        => _sources.TryGetValue(source.Id, out var type) ? type : null;
}
