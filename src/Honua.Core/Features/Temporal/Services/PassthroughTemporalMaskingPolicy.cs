// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Abstractions;

namespace Honua.Core.Features.Temporal.Services;

/// <summary>
/// Default temporal masking policy that redacts nothing (honua-server#1166). Deployments that need
/// field-level redaction of historical values override <see cref="ITemporalMaskingPolicy"/> in DI; the
/// temporal diff and timeline surfaces apply whatever policy is registered so history reads honor the
/// same redaction as current reads.
/// </summary>
public sealed class PassthroughTemporalMaskingPolicy : ITemporalMaskingPolicy
{
    /// <inheritdoc />
    public bool IsFieldMasked(string serviceId, int layerId, string field) => false;
}
