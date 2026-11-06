// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

public interface IObjectStoreTriggerProbe
{
    Task<ObjectStoreProbeResult?> ProbeNewestAsync(
        ObjectStoreTriggerConfig config,
        CancellationToken cancellationToken = default);
}

public readonly record struct ObjectStoreProbeResult
{
    public required string Key { get; init; }

    public required DateTimeOffset LastModified { get; init; }

    // A new object marker must sort strictly after every marker already observed, using last-modified time and key so equal timestamps stay ordered.
    public string Marker => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{LastModified.ToUniversalTime():O}|{Key}");
}
