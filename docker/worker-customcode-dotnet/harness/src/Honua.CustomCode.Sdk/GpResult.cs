// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.CustomCode.Sdk;

/// <summary>Terminal status of a custom-code GP tool run.</summary>
public enum GpStatus
{
    /// <summary>The tool completed successfully.</summary>
    Succeeded,

    /// <summary>The tool failed; <see cref="GpResult.Message"/> carries the reason.</summary>
    Failed,
}

/// <summary>
/// The terminal result a tool returns from
/// <see cref="IGeoprocessingTool.ExecuteAsync"/>. Construct via
/// <see cref="Succeeded"/> / <see cref="Failed"/> rather than the constructor so the
/// status label stays canonical. This is the .NET mirror of the Python harness's
/// <c>GpResult</c>.
/// </summary>
public sealed class GpResult
{
    private GpResult(GpStatus status, string? message)
    {
        Status = status;
        Message = message;
    }

    /// <summary>The terminal status.</summary>
    public GpStatus Status { get; }

    /// <summary>An optional success message, or the failure reason when failed.</summary>
    public string? Message { get; }

    /// <summary><see langword="true"/> when <see cref="Status"/> is <see cref="GpStatus.Succeeded"/>.</summary>
    public bool Ok => Status == GpStatus.Succeeded;

    /// <summary>Build a succeeded result, optionally with a message.</summary>
    /// <param name="message">An optional success message.</param>
    public static GpResult Succeeded(string? message = null) => new(GpStatus.Succeeded, message);

    /// <summary>Build a failed result with a required non-empty reason.</summary>
    /// <param name="message">The failure reason (must be non-empty).</param>
    public static GpResult Failed(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("GpResult.Failed requires a non-empty message.", nameof(message));
        }

        return new GpResult(GpStatus.Failed, message);
    }
}
