// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Inference;

/// <summary>
/// Configuration for the delegated imagery/ML inference lane (#2241). Honua does
/// not bundle a model runtime: the <c>imagery.classify</c> process submits the
/// source raster and a model reference to a configured cloud inference endpoint
/// (SageMaker / Vertex AI / Azure ML / any HTTP model server speaking Honua's
/// inference contract) and
/// lands the returned classification raster or detected features as a GP job
/// artifact.
/// </summary>
/// <remarks>
/// The lane is intentionally DORMANT when unconfigured: there is no startup
/// validation and no eager connectivity probe, so a deployment that never sets
/// <c>Geoprocessing:ImageryInference:Provider</c> pays zero cost and the process
/// fails at execution time with a clear "no backend configured" message rather
/// than a silent stub or a fake result (mirrors the <c>raster.interpolate-kriging</c>
/// advertised-but-unavailable posture).
/// </remarks>
internal sealed class ImageryInferenceOptions
{
    /// <summary>Configuration section the options bind from.</summary>
    public const string SectionName = "Geoprocessing:ImageryInference";

    /// <summary>Environment-variable fallback for the backend API key.</summary>
    public const string ApiKeyEnvironmentVariable = "HONUA_IMAGERY_INFERENCE_API_KEY";

    /// <summary>
    /// Inference backend provider id. Empty (the default) means no backend is
    /// configured and the imagery lane advertises itself as unavailable.
    /// Supported: <c>http</c> (generic REST speaking Honua's own JSON inference
    /// contract — implemented directly by a model server, or by a thin gateway in
    /// front of one such as an Azure ML online endpoint; NOT the OpenAI
    /// chat-completions wire format).
    /// Recognized but not yet supported in this build: <c>sagemaker</c>,
    /// <c>vertex</c>, <c>azureml</c> (their SDK-authenticated adapters); these
    /// fail with a clear message pointing at the <c>http</c> adapter.
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>
    /// Absolute http(s) invocation URL of the inference endpoint. Required for
    /// the <c>http</c> provider. Never echoed into job status messages.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// API key or bearer token for the endpoint. May be a secret reference
    /// resolved through the registered <c>ISecretProvider</c> (preferred), a
    /// literal value, or empty to fall back to the
    /// <see cref="ApiKeyEnvironmentVariable"/> environment variable. Never
    /// logged and never included in job status messages.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Optional custom request header to carry the API key (for example
    /// <c>x-api-key</c>). When empty (the default) the key is sent as an
    /// <c>Authorization: Bearer</c> header.
    /// </summary>
    public string ApiKeyHeader { get; set; } = "";

    /// <summary>
    /// Optional default model reference used when a job does not supply the
    /// <c>model</c> input.
    /// </summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>
    /// Per-request delegation timeout in seconds. Defaults to 300 (cloud
    /// inference on large scenes can be slow). Clamped to [1, 3600] at use.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Whether any backend provider has been configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Provider);
}
