// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Admission;

public sealed class AiExecutionAdmissionOptions
{
    public const string SectionName = "AiExecutionAdmission";

    public string DeploymentId { get; set; } = string.Empty;

    // An admission-backend failure never falls back to the other backend.
    public string Backend { get; set; } = "auto";
}
