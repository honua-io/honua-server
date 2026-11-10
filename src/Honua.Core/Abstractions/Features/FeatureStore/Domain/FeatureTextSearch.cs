// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public sealed record FeatureTextSearch(
    IReadOnlyList<string> Fields,
    IReadOnlyList<IReadOnlyList<FeatureSearchTerm>> Groups);

// The term is literal text, never a SQL or LIKE pattern.
public sealed record FeatureSearchTerm(string Text, bool Negated);
