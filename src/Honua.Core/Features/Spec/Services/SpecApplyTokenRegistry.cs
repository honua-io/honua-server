// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// Process-local registry mapping apply tokens to their
/// <see cref="CancellationTokenSource"/>. Allows
/// <c>POST /v1/spec/cancel</c> to cooperatively trip an in-flight run.
/// </summary>
internal sealed class SpecApplyTokenRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _entries =
        new(StringComparer.Ordinal);

    public void Register(string applyToken, CancellationTokenSource cts)
    {
        ArgumentException.ThrowIfNullOrEmpty(applyToken);
        ArgumentNullException.ThrowIfNull(cts);
        _entries[applyToken] = cts;
    }

    public bool TryCancel(string applyToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(applyToken);
        if (!_entries.TryGetValue(applyToken, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Run already released the token; treat as resolved.
        }

        return true;
    }

    public void Release(string applyToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(applyToken);
        _entries.TryRemove(applyToken, out _);
    }

    /// <summary>
    /// Returns whether the token is known. Used by endpoints to distinguish
    /// <c>404 apply-token-unknown</c> from successful cancellation.
    /// </summary>
    public bool Contains(string applyToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(applyToken);
        return _entries.ContainsKey(applyToken);
    }
}
