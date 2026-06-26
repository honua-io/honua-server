// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Builds the per-process-id routing table a geoprocessing dispatcher uses to
/// fan a claimed job out to the matching <see cref="IProcessExecutor"/>. Shared by
/// the managed <c>GeoprocessingDispatchJobExecutor</c> and the native
/// <c>GdalDispatchJobExecutor</c> so both get identical duplicate/empty-id
/// diagnostics from a single auto-registration loop (issue #2122).
/// </summary>
public static class ProcessExecutorRouteTable
{
    /// <summary>
    /// Builds an ordinal <c>processId -&gt; executor</c> map from the registered
    /// executor set. Throws <see cref="InvalidOperationException"/> when an executor
    /// declares no ids, declares a null/whitespace id, or when two executors claim
    /// the same id — surfacing authoring mistakes at composition time rather than
    /// silently dropping a process at runtime.
    /// </summary>
    /// <param name="executors">The DI-resolved per-process executors.</param>
    /// <returns>A frozen ordinal lookup keyed by process id.</returns>
    public static FrozenDictionary<string, IProcessExecutor> Build(
        IEnumerable<IProcessExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);

        var map = new Dictionary<string, IProcessExecutor>(StringComparer.Ordinal);

        foreach (var executor in executors)
        {
            ArgumentNullException.ThrowIfNull(executor);

            var ids = executor.ProcessIds;
            if (ids is null || ids.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Process executor '{executor.GetType().FullName}' declared no process ids; "
                    + "every IProcessExecutor must handle at least one canonical process id.");
            }

            foreach (var id in ids)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException(
                        $"Process executor '{executor.GetType().FullName}' declared a null or "
                        + "whitespace process id; process ids must be non-empty dotted identifiers.");
                }

                if (map.TryGetValue(id, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Duplicate geoprocessing process id '{id}' declared by both "
                        + $"'{existing.GetType().FullName}' and '{executor.GetType().FullName}'. "
                        + "Each process id must be handled by exactly one executor.");
                }

                map[id] = executor;
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
