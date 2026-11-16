// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using NSubstitute;

namespace Honua.Server.Tests.Import;

internal static class ImportExecutionTestJobs
{
    public static IExecutionJobStore Create()
    {
        var records = new ConcurrentDictionary<string, ExecutionJobRecord>(StringComparer.Ordinal);
        var store = Substitute.For<IExecutionJobStore>();
        store.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => records.TryAdd(call.Arg<ExecutionJobRecord>().OperationId, call.Arg<ExecutionJobRecord>()));
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => records.TryGetValue(call.Arg<string>(), out var record) ? record : null);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var next = call.Arg<ExecutionJobRecord>();
                return records.TryGetValue(next.OperationId, out var current) && current.Version == next.Version &&
                       records.TryUpdate(next.OperationId, next with { Version = next.Version + 1 }, current);
            });
        store.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<ExecutionJobRecord>)records.Values
                .Where(record => !record.Status.IsTerminal() &&
                    (call.Arg<ExecutionJobKind?>() is not { } kind || record.Spec.Kind == kind)).ToArray());
        return store;
    }

    public static async Task ObserveAsync(Task execution)
    {
        try
        {
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // The shared worker owns the durable cancellation outcome.
        }
    }
}
