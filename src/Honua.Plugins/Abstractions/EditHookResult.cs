// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

public readonly record struct EditHookResult
{
    private EditHookResult(bool isRejected, int errorCode, string? reason)
    {
        IsRejected = isRejected;
        ErrorCode = errorCode;
        Reason = reason;
    }

    public bool IsRejected { get; }

    public int ErrorCode { get; }

    public string? Reason { get; }

    public static EditHookResult Continue() => new(isRejected: false, errorCode: 0, reason: null);

    public static EditHookResult Reject(string reason, int code = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new EditHookResult(isRejected: true, errorCode: code, reason: reason);
    }
}
