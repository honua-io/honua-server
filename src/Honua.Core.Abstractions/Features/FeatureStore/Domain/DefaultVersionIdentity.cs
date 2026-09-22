// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>The kind of a canonical version identity.</summary>
public enum VersionIdentityKind
{
    /// <summary>The base managed feature store, with no branch overlay.</summary>
    Default,

    /// <summary>A branch with isolated version edits.</summary>
    Branch,
}

/// <summary>
/// Durable identity of the managed store's DEFAULT version. This descriptor never becomes a
/// <see cref="GdbVersion"/> or a non-null <see cref="VersionContext.VersionId"/>.
/// </summary>
public readonly record struct DefaultVersionIdentity
{
    /// <summary>The canonical DEFAULT display name; its namespace is not an authorization grant.</summary>
    public const string CanonicalName = "sde.DEFAULT";

    /// <summary>Recognizes reserved base-store aliases without granting any authorization.</summary>
    /// <param name="name">Requested name.</param>
    /// <returns>True for DEFAULT or the canonical qualified alias.</returns>
    public static bool IsDefaultName(string? name)
        => string.Equals(name?.Trim(), "DEFAULT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name?.Trim(), CanonicalName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Persisted, non-nil identity allocated once for the managed store.</summary>
    public required Guid VersionId { get; init; }

    /// <summary>Time the durable identity was allocated, not an invented data-modification time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    // Keep invariant metadata on the descriptor instance, including default(DefaultVersionIdentity).
    // Initialized auto-properties would change the default struct value contract.
#pragma warning disable CA1822 // Canonical identity descriptors expose instance metadata.
    /// <summary>Distinguishes this descriptor from a mutable branch.</summary>
    public VersionIdentityKind Kind => VersionIdentityKind.Default;

    /// <summary>The existing canonical name for the base store.</summary>
    public string VersionName => CanonicalName;

    /// <summary>Display namespace only. Matching this string never confers owner permissions.</summary>
    public string DisplayOwner => "sde";

    /// <summary>No additional version restriction; canonical service/resource authorization still applies.</summary>
    public VersionAccess Access => VersionAccess.Public;
#pragma warning restore CA1822
}
