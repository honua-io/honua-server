// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Security.Admission;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiIncidentWorkflowBinding
{
    public string Workflow { get; init; } = string.Empty;
    public string AuthorityScope { get; init; } = string.Empty;
    public Guid EpisodeId { get; init; }
    public long LifecycleVersion { get; init; }
    [JsonIgnore]
    public bool IsValid => Workflow is { Length: > 4 and <= 4096 } && Workflow.StartsWith("job:", StringComparison.Ordinal)
        && Workflow.All(c => !char.IsControl(c)) && AuthorityScope is { Length: > 0 }
        && Encoding.UTF8.GetByteCount(AuthorityScope) <= 256 && AuthorityScope.All(c => !char.IsControl(c))
        && EpisodeId != Guid.Empty && LifecycleVersion > 0;
    [JsonIgnore]
    public string WorkflowKey => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Workflow)));
}
