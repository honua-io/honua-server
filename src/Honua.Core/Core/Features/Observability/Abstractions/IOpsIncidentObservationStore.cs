// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

public interface IOpsIncidentObservationStore
{
    Task<OpsIncidentObservation> AppendAsync(
        OpsIncidentObservation observation, CancellationToken cancellationToken = default);

    Task<OpsIncidentObservationPage> ReadAsync(
        OpsIncidentObservationQuery query, CancellationToken cancellationToken = default);
}
