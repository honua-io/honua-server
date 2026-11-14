// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

internal sealed class ConcurrentCancellationStore
{
    private readonly ExecutionJobRecord _initial;
    private readonly TaskCompletionSource _bothWriting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ExecutionJobRecord _current;
    private int _writers;

    public ConcurrentCancellationStore(ExecutionJobRecord initial)
    {
        _initial = initial;
        _current = initial;
        Store.GetAsync(initial.OperationId, Arg.Any<CancellationToken>()).Returns(_ => _current);
        Store.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());
        Store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => TryWriteAsync(call.Arg<ExecutionJobRecord>()));
    }

    public IExecutionJobStore Store { get; } = Substitute.For<IExecutionJobStore>();
    public ExecutionJobRecord Current => _current;
    public int Writers => _writers;

    private async Task<bool> TryWriteAsync(ExecutionJobRecord candidate)
    {
        if (Interlocked.Increment(ref _writers) == 2) _bothWriting.TrySetResult();
        await _bothWriting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return ReferenceEquals(Interlocked.CompareExchange(ref _current, candidate, _initial), _initial);
    }
}
