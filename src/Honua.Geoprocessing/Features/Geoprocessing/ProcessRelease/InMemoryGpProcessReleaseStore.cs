// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.ProcessRelease;

public sealed class InMemoryGpProcessReleaseStore : IGpProcessReleaseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GpProcessReleaseRecord> _releases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GpProcessBinding> _bindings = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public InMemoryGpProcessReleaseStore(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public async Task<GpProcessReleaseRecord> PublishAsync(GpProcessReleaseLock lockDocument, string publishedBy, CancellationToken cancellationToken = default)
        => (await PublishWithStatusAsync(lockDocument, publishedBy, cancellationToken).ConfigureAwait(false)).Record;

    public Task<(GpProcessReleaseRecord Record, bool Created)> PublishWithStatusAsync(GpProcessReleaseLock lockDocument, string publishedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedBy);
        lockDocument = GpProcessReleaseLock.Parse(lockDocument.ToCanonicalJson());
        var digest = lockDocument.Digest;
        lock (_gate)
        {
            var created = !_releases.TryGetValue(digest, out var existing);
            if (created)
            {
                existing = new GpProcessReleaseRecord
                {
                    Lock = lockDocument,
                    Digest = digest,
                    PublishedBy = publishedBy,
                    PublishedAt = _time.GetUtcNow(),
                };
                _releases[digest] = existing;
            }

            return Task.FromResult((CloneRelease(existing!), created));
        }
    }

    public Task<GpProcessReleaseRecord?> GetReleaseAsync(string digest, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_releases.TryGetValue(digest, out var release) ? CloneRelease(release) : null);
        }
    }

    public Task<IReadOnlyList<GpProcessReleaseRecord>> ListReleasesAsync(string? process, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<GpProcessReleaseRecord> list = _releases.Values
                .Where(r => process is null || string.Equals(r.Lock.Process.Name, process, StringComparison.Ordinal))
                .OrderByDescending(r => r.PublishedAt)
                .ThenBy(r => r.Digest, StringComparer.Ordinal)
                .Select(CloneRelease)
                .ToArray();
            return Task.FromResult(list);
        }
    }

    public Task<GpProcessBinding?> GetBindingAsync(string process, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_bindings.GetValueOrDefault(process));
        }
    }

    public Task<IReadOnlyList<GpProcessBinding>> ListBindingsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<GpProcessBinding> list = _bindings.Values.OrderBy(b => b.Process, StringComparer.Ordinal).ToArray();
            return Task.FromResult(list);
        }
    }

    private static GpProcessReleaseRecord CloneRelease(GpProcessReleaseRecord record)
        => record with { Lock = GpProcessReleaseLock.Parse(record.Lock.ToCanonicalJson()) };

    public Task<GpProcessBindingDecision> ActivateAsync(GpProcessBindingActivation activation, GpProcessReleaseEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activation);
        lock (_gate)
        {
            var release = _releases.GetValueOrDefault(activation.LockDigest);
            var current = _bindings.GetValueOrDefault(activation.Process);
            var decision = GpProcessReleaseActivationPolicy.Evaluate(activation, release, current, environment, _time.GetUtcNow());
            if (decision.Activated)
            {
                _bindings[activation.Process] = decision.Binding!;
            }

            return Task.FromResult(decision);
        }
    }
}
