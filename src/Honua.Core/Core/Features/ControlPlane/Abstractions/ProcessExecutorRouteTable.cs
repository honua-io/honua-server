// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public static class ProcessExecutorRouteTable
{
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
