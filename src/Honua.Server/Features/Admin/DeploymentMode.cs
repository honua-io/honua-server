// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

/// <summary>
/// Defines the deployment mode for the Honua server.
/// </summary>
public enum DeploymentMode
{
    /// <summary>
    /// Single instance deployment (default).
    /// </summary>
    SingleInstance = 0,

    /// <summary>
    /// Multi-node cluster deployment.
    /// </summary>
    MultiNode = 1,

    /// <summary>
    /// Development mode.
    /// </summary>
    Development = 2,

    /// <summary>
    /// Staging environment deployment.
    /// </summary>
    Staging = 3,

    /// <summary>
    /// Production environment deployment.
    /// </summary>
    Production = 4
}
