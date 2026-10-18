// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>Captures the authenticated current agent request through the host credential authority.</summary>
/// <remarks>
/// Callers cannot supply tenant selection, arbitrary claims or a replacement principal.
/// The host reads its validated request identity and effective request tenant. This port
/// captures metadata only; it neither issues execution authority nor extends a credential.
/// </remarks>
public interface IAgentJobSecurityContextCapture
{
    /// <summary>Captures bounded trusted policy inputs and the current canonical credential snapshot.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The immutable captured policy and credential context.</returns>
    Task<JobSecurityContext> CaptureCurrentAsync(CancellationToken cancellationToken = default);
}
