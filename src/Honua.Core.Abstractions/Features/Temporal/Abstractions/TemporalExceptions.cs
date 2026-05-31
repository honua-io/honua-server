// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Abstractions;

/// <summary>
/// Thrown when a service/layer addressed by a temporal request cannot be resolved.
/// </summary>
public sealed class TemporalLayerNotFoundException : Exception
{
    /// <summary>Creates the exception with the given message.</summary>
    public TemporalLayerNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when a layer exists but does not support the requested temporal capability.
/// </summary>
public sealed class TemporalNotSupportedException : Exception
{
    /// <summary>Creates the exception with the given message.</summary>
    public TemporalNotSupportedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when a temporal request is malformed (e.g. conflicting cursors or negative generation).
/// </summary>
public sealed class TemporalValidationException : Exception
{
    /// <summary>Creates the exception with the given message.</summary>
    public TemporalValidationException(string message) : base(message)
    {
    }
}
