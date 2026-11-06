// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.ControlPlane.Lambda;

internal sealed class BatchJobStateChangeEvent
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("detail-type")]
    public string? DetailType { get; set; }

    [JsonPropertyName("detail")]
    public BatchJobStateChangeDetail? Detail { get; set; }
}

internal sealed class BatchJobStateChangeDetail
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("jobArn")]
    public string? JobArn { get; set; }

    [JsonPropertyName("jobName")]
    public string? JobName { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}
