// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IDagFeatureSource
{
    /// <summary>
    /// The source process id this reader handles (for example
    /// <c>source.esri-featureserver</c>). Matches the catalog
    /// <c>ProcessDefinition.ProcessId</c> and the dispatcher routing key.
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// Streams the features matching <paramref name="request"/> from the remote source,
    /// yielding them lazily as the underlying paginated reader advances.
    /// </summary>
    /// <param name="request">The resolved upstream query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of features.</returns>
    IAsyncEnumerable<DagSourceFeature> ReadAsync(
        DagSourceRequest request,
        CancellationToken cancellationToken = default);
}
