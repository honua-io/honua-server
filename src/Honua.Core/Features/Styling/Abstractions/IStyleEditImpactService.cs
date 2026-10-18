// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>Resolves the current caller's visible shared-style impact through canonical metadata access policy.</summary>
public interface IStyleEditImpactService
{
    /// <summary>Returns visible associations without disclosing hidden resource references.</summary>
    Task<StyleEditImpact?> GetAsync(string styleId, CancellationToken cancellationToken = default);
}

/// <summary>An authorized resource affected by a shared-style edit.</summary>
public sealed record StyleEditAssociation(string ResourceId, string Name, string? Title, string? ServiceId);

/// <summary>Reviewable visible shared-style scope; mutation additionally requires complete authorized impact.</summary>
public sealed record StyleEditImpact(string StyleId, int StyleVersion, string Etag, string ContentHash,
    bool CanEdit, string? ImpactHash, IReadOnlyList<StyleEditAssociation> Associations)
{
    /// <summary>Authorized saved/draft map and app consumers, distinct from direct layer/service associations.</summary>
    public IReadOnlyList<StyleEditConsumer> Consumers { get; init; } = [];
    /// <summary>True only when all actual indexed and associated scope was resolved and authorized.</summary>
    public bool ImpactComplete { get; init; }

    /// <summary>Server-resolved layer binding, excluded from the wire contract.</summary>
    [JsonIgnore]
    public int? CanonicalLayerId { get; init; }

    /// <summary>Server-resolved complete association pin, excluded from the wire contract.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> AssociatedLayerIds { get; init; } = [];
}

/// <summary>
/// A visible canonical Studio consumer; no hidden owner or tenant details are exposed.
/// ContentHash is the canonical immutable version hash, or the stored envelope snapshot digest for a mutable draft.
/// </summary>
public sealed record StyleEditConsumer(string Kind, Guid ItemId, string PackageKey, Guid? VersionId,
    Guid? DraftId, long? Generation, string ContentHash, string PublicationState, bool IsCurrent);
