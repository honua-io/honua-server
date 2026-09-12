// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane;

namespace Honua.Core.Tests.Features.ControlPlane;

/// <summary>
/// Pins the on-disk qualification receipt contract read by
/// <c>scripts/qualification/gp-lifecycle-harness.sh</c>. The barrier ships inside the
/// trimmed/native-AOT worker, so its receipts are written through a source-generated
/// serializer; these tests prove that swap did not move a single JSON property name.
/// </summary>
public class ExecutionQualificationBarrierTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"honua-barrier-{Guid.NewGuid():N}");
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(
        ExecutionQualificationBarrier.RootEnvironmentVariable);

    public ExecutionQualificationBarrierTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(ExecutionQualificationBarrier.RootEnvironmentVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ExecutionQualificationBarrier.RootEnvironmentVariable, _previousRoot);
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task WaitAsync_Released_WritesReadyAndReleasedReceiptsWithStableFields()
    {
        using var scope = ExecutionQualificationBarrier.Begin("op-1", "worker-7");
        var operationDirectory = Path.Join(_root, "op-1");
        var readyPath = Path.Join(operationDirectory, "native-process-started.ready.json");

        var wait = ExecutionQualificationBarrier.WaitAsync(
            "native-process-started", CancellationToken.None, childProcessId: 4242);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(readyPath))
        {
            await Task.Delay(10, timeout.Token);
        }

        var ready = JsonDocument.Parse(await File.ReadAllTextAsync(readyPath));
        Assert.Equal(
            [
                "operationId", "workerId", "barrier", "readyAt",
                "workerProcessId", "childProcessId", "executorIgnoresCancellation"
            ],
            ready.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("op-1", ready.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("worker-7", ready.RootElement.GetProperty("workerId").GetString());
        Assert.Equal("native-process-started", ready.RootElement.GetProperty("barrier").GetString());
        Assert.Equal(Environment.ProcessId, ready.RootElement.GetProperty("workerProcessId").GetInt32());
        Assert.Equal(4242, ready.RootElement.GetProperty("childProcessId").GetInt32());
        Assert.False(ready.RootElement.GetProperty("executorIgnoresCancellation").GetBoolean());
        Assert.True(ready.RootElement.GetProperty("readyAt").TryGetDateTimeOffset(out _));

        await File.WriteAllTextAsync(
            Path.Join(operationDirectory, "native-process-started.release"), string.Empty);
        await wait.WaitAsync(TimeSpan.FromSeconds(10));

        var released = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Join(operationDirectory, "native-process-started.released.json")));
        Assert.Equal(
            ["operationId", "workerId", "barrier", "releasedAt", "workerProcessId", "childProcessId"],
            released.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(4242, released.RootElement.GetProperty("childProcessId").GetInt32());
        Assert.True(released.RootElement.GetProperty("releasedAt").TryGetDateTimeOffset(out _));
    }

    [Fact]
    public async Task WaitAsync_Cancelled_WritesSignalObservedReceiptWithNullChildProcessId()
    {
        using var scope = ExecutionQualificationBarrier.Begin("op/2", "worker-8");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExecutionQualificationBarrier.WaitAsync("native-process-started", cancelled.Token));

        // The operation id is path-sanitized before it becomes a directory name.
        var observed = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Join(_root, "op_2", "native-process-started.signal-observed.json")));
        Assert.Equal(
            [
                "operationId", "workerId", "barrier", "observedAt",
                "workerProcessId", "childProcessId", "tokenCancelled"
            ],
            observed.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("op/2", observed.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(JsonValueKind.Null, observed.RootElement.GetProperty("childProcessId").ValueKind);
        Assert.True(observed.RootElement.GetProperty("tokenCancelled").GetBoolean());
        Assert.True(observed.RootElement.GetProperty("observedAt").TryGetDateTimeOffset(out _));
    }
}
