// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin;

internal sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    [Required]
    public string Mode { get; set; } = "SingleInstance";

    public bool EnableDeploymentControl { get; set; }

    public int MaxRollbackHistory { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 300;

    public bool EnablePreflightChecks { get; set; } = true;
}
