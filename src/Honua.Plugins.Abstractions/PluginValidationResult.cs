// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Outcome of a plugin feature validation. A successful result allows the edit to proceed;
/// a failed result rejects the individual feature (and, when the request asks to roll back on
/// failure, the whole transaction). Shapes intentionally mirror the platform's
/// <c>FieldValidationError</c> so failures map cleanly onto protocol edit error envelopes.
/// </summary>
public readonly record struct PluginValidationResult
{
    private PluginValidationResult(bool isValid, int errorCode, string? errorMessage)
    {
        IsValid = isValid;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>Gets whether the feature passed validation.</summary>
    public bool IsValid { get; }

    /// <summary>Gets the protocol-facing error code when <see cref="IsValid"/> is <see langword="false"/>.</summary>
    public int ErrorCode { get; }

    /// <summary>Gets the safe, client-facing error message when <see cref="IsValid"/> is <see langword="false"/>.</summary>
    public string? ErrorMessage { get; }

    /// <summary>A successful validation result.</summary>
    public static PluginValidationResult Success() => new(isValid: true, errorCode: 0, errorMessage: null);

    /// <summary>
    /// A failed validation result.
    /// </summary>
    /// <param name="message">Safe, client-facing reason the feature was rejected.</param>
    /// <param name="code">Optional protocol error code (defaults to a generic invalid-feature code).</param>
    public static PluginValidationResult Error(string message, int code = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new PluginValidationResult(isValid: false, errorCode: code, errorMessage: message);
    }
}
