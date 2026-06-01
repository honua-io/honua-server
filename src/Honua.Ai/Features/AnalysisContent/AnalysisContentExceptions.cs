// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.AnalysisContent;

/// <summary>
/// Thrown when an analysis content request body fails validation. Carries a stable machine-readable
/// <see cref="Code"/> and an optional <see cref="Path"/> (JSON Pointer / dotted field path) so the endpoint
/// can surface a field-addressable RFC-7807 ProblemDetails <c>errors[]</c> rather than a flat message.
/// </summary>
internal sealed class AnalysisContentValidationException : Exception
{
    public AnalysisContentValidationException(string message, string code, string? path = null)
        : base(message)
    {
        Code = code;
        Path = path;
    }

    /// <summary>Stable machine-readable error code (e.g. <c>analysis.content.name.required</c>).</summary>
    public string Code { get; }

    /// <summary>Optional JSON Pointer / dotted field path of the offending value (e.g. <c>/inputs/0/layerId</c>).</summary>
    public string? Path { get; }
}

internal sealed class AnalysisContentNotFoundException(string message) : Exception(message);

internal sealed class AnalysisContentConflictException(string message) : Exception(message);
