// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Classifies provider-specific exceptions as "object not found" so cloud-neutral
/// orchestrators (e.g. the PMTiles range proxy) can distinguish a missing
/// underlying object from a genuine 5xx error without taking a direct dependency
/// on the AWS / Azure SDK exception hierarchies. Per-cloud implementations live
/// in Honua.Aws / Honua.Azure and are registered as <see cref="IEnumerable{T}"/>
/// so the orchestrator can fan a single exception through every classifier.
/// </summary>
public interface ICloudNotFoundClassifier
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="exception"/> is recognized as a
    /// "missing object" / 404-equivalent surface error for this classifier's
    /// provider. Implementations must return <c>false</c> for unfamiliar types
    /// rather than throwing, so chained classifiers compose cleanly.
    /// </summary>
    bool IsNotFound(Exception exception);
}
