// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization;

/// <summary>
/// Ambient marker for "this call chain is executing a background job on behalf of a submitting
/// principal" (honua-server#3068). Row-level security and field masking are otherwise
/// request-scoped concerns resolved from <c>IHttpContextAccessor.HttpContext?.User</c>, which is
/// always <see langword="null"/> on the job worker; this scope is the request-context-free
/// replacement that carries the submitter's pinned <see cref="JobSecurityContext"/> down to the
/// feature store.
/// </summary>
/// <remarks>
/// <para>
/// Stored on an <see cref="AsyncLocal{T}"/> — the same pattern
/// <c>FeatureMutationOutboxScope</c> uses — so it flows through awaits without threading a new
/// argument through every <c>IStreamingFeatureStore</c> consumer.
/// </para>
/// <para>
/// The scope is deliberately begun even when no submitter snapshot exists
/// (<see cref="JobSecurityScopeState.Submitter"/> is <see langword="null"/>). That combination —
/// "inside a job, with no captured identity" — is the FAIL-CLOSED signal: a job record written
/// before the snapshot existed, or by a path that does not capture one, must be refused at the
/// catalog-layer read seam rather than silently reading rows and fields the submitter may not
/// see. A call chain with no scope at all is an ordinary request thread and is unaffected.
/// </para>
/// <para>
/// <see cref="Begin"/> must be invoked synchronously in the caller's own async flow:
/// <see cref="AsyncLocal{T}"/> writes made inside an <c>async</c> callee are not observed by the
/// caller after the awaiter completes.
/// </para>
/// </remarks>
public static class JobSecurityScope
{
    private static readonly AsyncLocal<JobSecurityScopeState?> Ambient = new();

    /// <summary>
    /// The active job-execution scope for the current async flow, or <see langword="null"/> when
    /// the call chain is not executing a background job (an ordinary request thread, an
    /// in-process tool, or a test).
    /// </summary>
    public static JobSecurityScopeState? Current => Ambient.Value;

    /// <summary>
    /// Begins a job-execution scope carrying <paramref name="submitter"/> (which may be
    /// <see langword="null"/> for a job record with no captured snapshot — see the fail-closed
    /// note on this type). Returns an <see cref="IDisposable"/> that restores the previous scope,
    /// so it must always be used from a <c>using</c> statement or the scope leaks onto a pooled
    /// worker thread.
    /// </summary>
    /// <param name="submitter">The pinned submitter snapshot, when the job record carries one.</param>
    /// <returns>A disposable restoring the previous ambient scope.</returns>
    public static IDisposable Begin(JobSecurityContext? submitter)
    {
        var previous = Ambient.Value;
        Ambient.Value = new JobSecurityScopeState(submitter);
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

/// <summary>
/// State carried by an active <see cref="JobSecurityScope"/>.
/// </summary>
/// <param name="Submitter">
/// The submitter snapshot pinned on the job record, or <see langword="null"/> when the record
/// carries none. A <see langword="null"/> submitter inside an active scope means the read cannot
/// be constrained to the caller and must therefore be refused.
/// </param>
public sealed record JobSecurityScopeState(JobSecurityContext? Submitter);
