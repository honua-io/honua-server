// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Wfs20.Models;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Service responsible for handling WFS 2.0 capabilities operations.
/// Segregated interface following the Interface Segregation Principle.
/// </summary>
internal interface IWfs20CapabilitiesService
{
    /// <summary>
    /// Generate WFS 2.0 capabilities document
    /// </summary>
    /// <param name="context">HTTP context for authorization and request details</param>
    /// <param name="acceptVersions">Accepted WFS versions</param>
    /// <param name="requestedSections">Specific sections requested</param>
    /// <param name="baseUrl">Base URL for the service</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>WFS capabilities document</returns>
    Task<WfsCapabilities> GetCapabilitiesAsync(
        HttpContext context,
        string? acceptVersions,
        IReadOnlySet<string>? requestedSections,
        string baseUrl,
        CancellationToken cancellationToken = default);
}