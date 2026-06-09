// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Outcome of an <see cref="IEditHook.OnBeforeEditAsync"/> call. A hook may allow the batch to
/// continue or reject the entire batch with a reason.
/// </summary>
public readonly record struct EditHookResult
{
    private EditHookResult(bool isRejected, int errorCode, string? reason)
    {
        IsRejected = isRejected;
        ErrorCode = errorCode;
        Reason = reason;
    }

    /// <summary>Gets whether the hook rejected the edit batch.</summary>
    public bool IsRejected { get; }

    /// <summary>Gets the protocol-facing error code when <see cref="IsRejected"/> is <see langword="true"/>.</summary>
    public int ErrorCode { get; }

    /// <summary>Gets the safe, client-facing rejection reason when <see cref="IsRejected"/> is <see langword="true"/>.</summary>
    public string? Reason { get; }

    /// <summary>Allow the edit batch to proceed.</summary>
    public static EditHookResult Continue() => new(isRejected: false, errorCode: 0, reason: null);

    /// <summary>
    /// Reject the entire edit batch.
    /// </summary>
    /// <param name="reason">Safe, client-facing reason the batch was rejected.</param>
    /// <param name="code">Optional protocol error code (defaults to a generic invalid-feature code).</param>
    public static EditHookResult Reject(string reason, int code = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new EditHookResult(isRejected: true, errorCode: code, reason: reason);
    }
}
