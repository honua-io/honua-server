// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Base structure for service errors across all protocols
/// </summary>
public readonly record struct ServiceError
{
    /// <summary>
    /// Error code (numeric for GeoServices, string for others)
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Error message or description
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional target or field that caused the error
    /// </summary>
    public string? Target { get; init; }

    /// <summary>
    /// Additional error details or nested errors
    /// </summary>
    public IReadOnlyList<string>? Details { get; init; }

    /// <summary>
    /// Creates a simple error with code and message
    /// </summary>
    /// <param name="code">Error code</param>
    /// <param name="message">Error message</param>
    /// <returns>Service error instance</returns>
    public static ServiceError Create(string code, string message)
        => new() { Code = code, Message = message };

    /// <summary>
    /// Creates an error with code, message, and target
    /// </summary>
    /// <param name="code">Error code</param>
    /// <param name="message">Error message</param>
    /// <param name="target">Error target</param>
    /// <returns>Service error instance</returns>
    public static ServiceError Create(string code, string message, string target)
        => new() { Code = code, Message = message, Target = target };

    /// <summary>
    /// Creates a comprehensive error with all details
    /// </summary>
    /// <param name="code">Error code</param>
    /// <param name="message">Error message</param>
    /// <param name="target">Error target</param>
    /// <param name="details">Additional error details</param>
    /// <returns>Service error instance</returns>
    public static ServiceError Create(string code, string message, string? target, IReadOnlyList<string>? details)
        => new() { Code = code, Message = message, Target = target, Details = details };

    /// <summary>
    /// Creates an error from a numeric code (for GeoServices compatibility)
    /// </summary>
    /// <param name="code">Numeric error code</param>
    /// <param name="message">Error message</param>
    /// <returns>Service error instance</returns>
    public static ServiceError Create(int code, string message)
        => new() { Code = code.ToString(CultureInfo.InvariantCulture), Message = message };

    /// <summary>
    /// Gets the error code as an integer if possible
    /// </summary>
    /// <returns>Numeric error code or null if not numeric</returns>
    public int? GetNumericCode()
        => int.TryParse(Code, out var numericCode) ? numericCode : null;
}
