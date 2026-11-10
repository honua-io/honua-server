// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

public interface IFeatureOutputFormat
{
    string FormatId { get; }

    string MediaType { get; }

    string FileExtension { get; }

    ValueTask<long> WriteAsync(
        IAsyncEnumerable<Feature> features,
        FeatureOutputFormatContext context,
        Stream output,
        CancellationToken cancellationToken);
}
