// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure.Core;
using Azure.Identity;

namespace Honua.ControlPlane;

/// <summary>
/// Provides a single process-wide <see cref="DefaultAzureCredential"/> shared by the ARM/data-plane
/// REST clients in this module.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DefaultAzureCredential"/> (and the chained credentials it wraps) own an internal
/// <c>HttpPipeline</c>/handler and are designed to be long-lived and reused for the lifetime of the
/// process. Constructing one per client and never disposing it would leak those handlers if a client
/// were ever registered with a transient/scoped lifetime.
/// </para>
/// <para>
/// Sharing a single static instance removes the per-client allocation and makes credential lifetime
/// independent of client lifetime. The instance lives for the lifetime of the process, so there is no
/// disposal site; this mirrors the recommended long-lived usage of <see cref="DefaultAzureCredential"/>.
/// Tests can still inject an alternate <see cref="TokenCredential"/> via the internal constructors.
/// </para>
/// </remarks>
internal static class AzureManagementCredential
{
    /// <summary>
    /// The shared, process-lifetime credential instance.
    /// </summary>
    public static TokenCredential Shared { get; } = new DefaultAzureCredential();
}
