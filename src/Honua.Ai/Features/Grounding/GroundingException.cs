// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Grounding.Domain;

namespace Honua.Ai.Grounding;

/// <summary>
/// Exception family for grounding-service failures. Kept separate from
/// <see cref="Geoprocessing.GeoprocessingValidationException"/> because some
/// grounding failures (catalog outage, unknown intent) are not validation
/// errors, even though they surface through the same MCP error channel.
/// </summary>
internal sealed class GroundingException : Exception
{
    /// <summary>
    /// Typed error kind for caller-side branching and telemetry.
    /// </summary>
    public GroundingErrorKind Kind { get; }

    public GroundingException(GroundingErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public GroundingException(GroundingErrorKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
