// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Effective Share access tier for a Console content item. This is the share
/// facet of an item and is intentionally distinct from <see cref="ConsoleVisibility"/>:
/// visibility captures <em>membership</em> scope (who, by identity, may see the
/// item) while the access tier captures how the item is <em>shared</em> beyond
/// membership (opaque links, public indexing). A <c>public-link</c> tier never
/// changes membership visibility; <c>public-indexed</c> implies the item is
/// publicly readable and eligible for catalog indexing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleShareAccessTier>))]
public enum ConsoleShareAccessTier
{
    /// <summary>
    /// Item is shared only with its membership scope (effective visibility is at
    /// or below organization) and exposes no public-link access.
    /// </summary>
    [JsonStringEnumMemberName("private")]
    Private,

    /// <summary>
    /// Item is shared with the whole organization (membership scope).
    /// </summary>
    [JsonStringEnumMemberName("organization")]
    Organization,

    /// <summary>
    /// Item is reachable via an opaque public-link token regardless of membership
    /// visibility. Anonymous callers presenting a valid token may read a
    /// presentation-safe projection.
    /// </summary>
    [JsonStringEnumMemberName("public-link")]
    PublicLink,

    /// <summary>
    /// Item visibility is public and the item is eligible for catalog indexing
    /// and open-data distribution. Anonymous reads are permitted.
    /// </summary>
    [JsonStringEnumMemberName("public-indexed")]
    PublicIndexed,
}
