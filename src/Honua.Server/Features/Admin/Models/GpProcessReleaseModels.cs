// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

public sealed class GpProcessReleaseResponse
{
    [JsonPropertyName("lockDigest")]
    public string LockDigest { get; init; } = string.Empty;

    [JsonPropertyName("process")]
    public string Process { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("sourceSha")]
    public string SourceSha { get; init; } = string.Empty;

    [JsonPropertyName("publishedBy")]
    public string PublishedBy { get; init; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset PublishedAt { get; init; }

    [JsonPropertyName("created")]
    public bool? Created { get; init; }
}

public sealed class GpProcessReleaseListResponse
{
    [JsonPropertyName("releases")]
    public IReadOnlyList<GpProcessReleaseResponse> Releases { get; init; } = [];
}

public sealed class GpProcessBindingTargetResponse
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("image")]
    public string Image { get; init; } = string.Empty;

    [JsonPropertyName("jobDefinitionArn")]
    public string? JobDefinitionArn { get; init; }
}

public sealed class GpProcessBindingResponse
{
    [JsonPropertyName("process")]
    public string Process { get; init; } = string.Empty;

    [JsonPropertyName("bindingVersion")]
    public int BindingVersion { get; init; }

    [JsonPropertyName("lockDigest")]
    public string? LockDigest { get; init; }

    [JsonPropertyName("previousLockDigest")]
    public string? PreviousLockDigest { get; init; }

    [JsonPropertyName("targets")]
    public IReadOnlyList<GpProcessBindingTargetResponse> Targets { get; init; } = [];

    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    [JsonPropertyName("activatedBy")]
    public string? ActivatedBy { get; init; }

    [JsonPropertyName("activatedAt")]
    public DateTimeOffset? ActivatedAt { get; init; }

    [JsonPropertyName("serverContractVersion")]
    public int ServerContractVersion { get; init; }

    [JsonPropertyName("supportedTargets")]
    public IReadOnlyList<string> SupportedTargets { get; init; } = [];

    // A binding change applies to the next scheduled fire and does not copy the schedule.
    [JsonPropertyName("scheduleReconciliation")]
    public string ScheduleReconciliation { get; init; } = string.Empty;
}

public sealed class GpProcessBindingListResponse
{
    [JsonPropertyName("bindings")]
    public IReadOnlyList<GpProcessBindingResponse> Bindings { get; init; } = [];
}

public sealed class GpProcessBindingTargetRequest
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("jobDefinitionArn")]
    public string? JobDefinitionArn { get; init; }
}

public sealed class GpProcessBindingActivateRequest
{
    [JsonPropertyName("lockDigest")]
    public string LockDigest { get; init; } = string.Empty;

    [JsonPropertyName("expectedBindingVersion")]
    public int ExpectedBindingVersion { get; init; }

    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    [JsonPropertyName("targets")]
    public IReadOnlyList<GpProcessBindingTargetRequest> Targets { get; init; } = [];

}

public sealed class GpProcessBindingRefusedResponse
{
    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> Reasons { get; init; } = [];

    [JsonPropertyName("details")]
    public IReadOnlyList<string> Details { get; init; } = [];

    [JsonPropertyName("binding")]
    public GpProcessBindingResponse? Binding { get; init; }
}
