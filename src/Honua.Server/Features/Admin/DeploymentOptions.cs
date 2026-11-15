// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin;

internal sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    [Required]
    public string Mode { get; set; } = "SingleInstance";

    public int TimeoutSeconds { get; set; } = 300;
}
