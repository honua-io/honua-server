// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Configuration options for deployment control and management.
/// </summary>
internal sealed class DeploymentOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Deployment";

    /// <summary>
    /// Deployment mode (SingleInstance or MultiNode).
    /// </summary>
    [Required]
    public string Mode { get; set; } = "SingleInstance";

    /// <summary>
    /// Whether deployment control endpoints are enabled.
    /// </summary>
    public bool EnableDeploymentControl { get; set; }

    /// <summary>
    /// Maximum rollback history to maintain.
    /// </summary>
    public int MaxRollbackHistory { get; set; } = 10;

    /// <summary>
    /// Deployment timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Whether to enable preflight checks.
    /// </summary>
    public bool EnablePreflightChecks { get; set; } = true;
}
