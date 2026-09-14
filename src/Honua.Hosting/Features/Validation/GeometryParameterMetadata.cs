// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Validation;

/// <summary>
/// Declares a structured geometry parameter on a routed endpoint. The shared
/// input middleware enforces its byte budget before invoking protocol parsing.
/// Other parameters retain their ordinary text limits and security checks.
/// </summary>
internal abstract class GeometryParameterMetadata
{
    public abstract string ParameterName { get; }

    /// <summary>
    /// Returns a validation error, or null for a bounded geometry accepted by
    /// the protocol parser. Called only after the geometry byte limit passes.
    /// </summary>
    public abstract string? Validate(string value, int maxVertices, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads supported query body encodings with the protocol's own parameter
    /// conversion rules, preserving the body for the downstream handler.
    /// </summary>
    public abstract Task<(IReadOnlyDictionary<string, StringValues>? Values, string? Error)> ReadBodyParametersAsync(
        HttpRequest request, CancellationToken cancellationToken);
}
