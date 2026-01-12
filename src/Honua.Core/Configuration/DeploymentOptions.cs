// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Deployment configuration for single-instance versus multi-node runtime modes.
/// </summary>
public sealed class DeploymentOptions
{
    /// <summary>
    /// Configuration section name for deployment settings.
    /// </summary>
    public const string SectionName = "Deployment";

    /// <summary>
    /// Gets or sets the deployment mode.
    /// </summary>
    public DeploymentMode Mode { get; set; } = DeploymentMode.SingleInstance;
}

/// <summary>
/// Supported deployment modes for Honua Server.
/// </summary>
public enum DeploymentMode
{
    /// <summary>
    /// Single-instance mode with local fallbacks enabled.
    /// </summary>
    SingleInstance = 0,

    /// <summary>
    /// Multi-node mode requiring Redis and shared storage.
    /// </summary>
    MultiNode = 1
}
