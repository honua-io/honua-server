// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;

namespace Honua.Core.Features.FeatureStore.Domain;

public sealed class AgentFeatureWriteApprovalRequiredException : ResourceAccessDeniedException
{
    public const string DefaultMessage =
        "Agent feature-data edits require human approval. Submit changes through an approved workflow instead of mutating feature data directly.";

    public override string Message => DefaultMessage;
}

public static class AgentFeatureWriteGuard
{
    private static readonly AsyncLocal<Func<bool>?> CurrentEvaluator = new();

    public static bool IsBlocked => CurrentEvaluator.Value?.Invoke() == true;

    public static void EnsureAllowed()
    {
        if (IsBlocked)
        {
            throw new AgentFeatureWriteApprovalRequiredException();
        }
    }

    public static IDisposable Begin(Func<bool> isBlocked)
    {
        ArgumentNullException.ThrowIfNull(isBlocked);
        var previous = CurrentEvaluator.Value;
        CurrentEvaluator.Value = isBlocked;
        return new Releaser(previous);
    }

    private sealed class Releaser(Func<bool>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentEvaluator.Value = previous;
            _disposed = true;
        }
    }
}
