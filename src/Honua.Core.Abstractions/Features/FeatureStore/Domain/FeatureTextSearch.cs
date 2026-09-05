// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Provider-neutral, case-insensitive literal substring search across text fields.
/// Groups are OR-ed; terms within a group are AND-ed. Null fields do not match.
/// </summary>
/// <param name="Fields">Declared text field names.</param>
/// <param name="Groups">Disjunction of term conjunctions.</param>
public sealed record FeatureTextSearch(
    IReadOnlyList<string> Fields,
    IReadOnlyList<IReadOnlyList<FeatureSearchTerm>> Groups);

/// <summary>A literal substring and optional negation across all searchable fields.</summary>
/// <param name="Text">Literal text, never a SQL or LIKE pattern.</param>
/// <param name="Negated">Whether none of the searchable fields may contain the text.</param>
public sealed record FeatureSearchTerm(string Text, bool Negated);
