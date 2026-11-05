// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

// A computed-field provider that throws is omitted from the response and must not fail the query.
public interface IComputedFieldProvider
{
    string FieldName { get; }

    ValueTask<object?> ComputeAsync(
        Feature feature,
        ComputedFieldContext context,
        CancellationToken cancellationToken);
}
