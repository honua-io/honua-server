// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public record ParameterizedQuery(string Sql, List<object> WhereParameters)
{
    public bool DisableJit { get; init; }
}
