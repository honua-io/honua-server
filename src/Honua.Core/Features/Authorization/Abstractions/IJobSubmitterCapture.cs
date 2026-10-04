// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Captures the security context of the caller that submits in-process background work, so the
/// work can run as that caller (inside a <see cref="JobSecurityScope"/>) after the submitting
/// request has ended.
/// </summary>
public interface IJobSubmitterCapture
{
    /// <summary>
    /// Captures the current caller: the active job's submitter when called inside a job,
    /// otherwise the request principal.
    /// </summary>
    /// <returns>The captured context, or <see langword="null"/> when there is no caller to capture.</returns>
    JobSecurityContext? CaptureCurrent();
}
