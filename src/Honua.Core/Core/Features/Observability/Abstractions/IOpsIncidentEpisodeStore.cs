// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

public interface IOpsIncidentEpisodeStore
{
    Task<OpsIncidentEpisode?> ObserveAsync(
        OpsIncidentEpisodeSignal signal, CancellationToken cancellationToken = default);

    Task<OpsIncidentEpisode?> ReadAsync(
        OpsIncidentEpisodeIdentity identity, CancellationToken cancellationToken = default);
}

public interface IOpsIncidentEpisodeResolver
{
    Task<OpsIncidentEpisode?> ResolveAsync(
        OpsIncidentEpisodeReference reference, CancellationToken cancellationToken = default);
}
