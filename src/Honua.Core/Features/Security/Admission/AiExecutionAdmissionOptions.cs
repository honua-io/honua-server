// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Admission;

/// <summary>Deployment identity and authoritative backend selection for model invocation admission.</summary>
public sealed class AiExecutionAdmissionOptions
{
    /// <summary>Configuration section shared by all model surfaces and admission controls.</summary>
    public const string SectionName = "AiExecutionAdmission";

    /// <summary>Stable deployment identity shared by all replicas; absent identity denies model invocations.</summary>
    public string DeploymentId { get; set; } = string.Empty;

    /// <summary>Authoritative backend: auto (registered Postgres, otherwise shared Redis), postgres or redis. Failures never trigger fallback.</summary>
    public string Backend { get; set; } = "auto";
}
