// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Domain;

namespace Honua.Core.Features.SensorThings.Abstractions;

public interface IObservationChangeEventPublisher
{
    // A publish failure must not fail the ingest write.
    void PublishObservations(IReadOnlyList<SensorThingsObservation> observations);
}
