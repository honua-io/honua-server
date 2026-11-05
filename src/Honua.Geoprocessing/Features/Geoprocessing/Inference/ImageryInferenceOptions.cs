// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing.Inference;

// An empty provider leaves the lane dormant: nothing validates it at startup and nothing probes the endpoint.
internal sealed class ImageryInferenceOptions
{
    public Honua.Core.Features.Security.AiDisclosurePolicyOptions Disclosure { get; set; } = new();

    public const string SectionName = "Geoprocessing:ImageryInference";

    public const string ApiKeyEnvironmentVariable = "HONUA_IMAGERY_INFERENCE_API_KEY";

    public string Provider { get; set; } = "";

    // The endpoint URL is never copied into job status.
    public string Endpoint { get; set; } = "";

    // The API key is never logged and never copied into job status.
    public string ApiKey { get; set; } = "";

    public string ApiKeyHeader { get; set; } = "";

    public string DefaultModel { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 300;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Provider);
}
