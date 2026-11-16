// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization;

public static class JobSecurityScope
{
    private static readonly AsyncLocal<JobSecurityScopeState?> Ambient = new();

    public static JobSecurityScopeState? Current => Ambient.Value;

    public static IDisposable Begin(JobSecurityContext? submitter)
    {
        var previous = Ambient.Value;
        Ambient.Value = new JobSecurityScopeState(submitter);
        return new ScopeReleaser(previous);
    }

    // Work that fills a store shared by every reader, such as the generated tile cache, reads as the
    // system: the same as a call chain outside any job. Disposing restores the job scope.
    public static IDisposable BeginSystem()
    {
        var previous = Ambient.Value;
        Ambient.Value = null;
        return new ScopeReleaser(previous);
    }

    private sealed class ScopeReleaser(JobSecurityScopeState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = previous;
        }
    }
}

public sealed record JobSecurityScopeState(JobSecurityContext? Submitter);
