// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Inference;

/// <summary>
/// Provider adapter contract for the delegated imagery/ML inference lane
/// (#2241). One adapter per backend family sits behind this single interface;
/// the executor selects the adapter whose <see cref="Provider"/> matches the
/// configured <see cref="ImageryInferenceOptions.Provider"/>. Adding a
/// SageMaker/Vertex SDK-authenticated adapter later is a new implementation
/// registered into the <c>IImageryInferenceClient</c> DI enumerable — no
/// executor change.
/// </summary>
internal interface IImageryInferenceClient
{
    /// <summary>Provider id this adapter serves (ordinal-ignore-case match).</summary>
    string Provider { get; }

    /// <summary>
    /// Submits one inference request to the configured backend and returns the
    /// validated outcome. Failures are surfaced as
    /// <see cref="ImageryInferenceException"/> whose message is SAFE to place on
    /// the job status (no endpoint URLs, credentials, or raw provider bodies).
    /// </summary>
    Task<ImageryInferenceOutcome> InferAsync(
        ImageryInferenceOptions options,
        ImageryInferenceRequest request,
        CancellationToken cancellationToken);
}
