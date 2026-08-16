// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>
/// Supplies the identity half of a deterministic package draft (ADR-0076).
/// </summary>
/// <remarks>
/// The draft factories are otherwise pure functions of their request. Identity
/// and time are the only inherently non-deterministic inputs, so both are
/// injected: a factory never calls <see cref="Guid.NewGuid"/> or
/// <c>DateTimeOffset.UtcNow</c> itself, which is what lets a test pin a draft
/// byte-for-byte.
/// </remarks>
public interface IDraftIdentifierGenerator
{
    /// <summary>
    /// Creates a new opaque draft identifier carrying the supplied prefix.
    /// </summary>
    /// <param name="prefix">
    /// Identifier prefix without the separator, for example <c>map</c> or
    /// <c>app</c>. The returned identifier is <c>{prefix}_{token}</c>.
    /// </param>
    /// <returns>A newly generated identifier.</returns>
    string NewIdentifier(string prefix);
}

/// <summary>
/// Default <see cref="IDraftIdentifierGenerator"/>: a GUID-backed token with the
/// requested prefix, yielding identifiers of the form <c>map_…</c> / <c>app_…</c>.
/// </summary>
public sealed class GuidDraftIdentifierGenerator : IDraftIdentifierGenerator
{
    /// <inheritdoc />
    public string NewIdentifier(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return string.Concat(prefix, "_", Guid.NewGuid().ToString("n"));
    }
}
