// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IDagFeatureSource
{
    string SourceId { get; }

    IAsyncEnumerable<DagSourceFeature> ReadAsync(
        DagSourceRequest request,
        CancellationToken cancellationToken = default);
}
