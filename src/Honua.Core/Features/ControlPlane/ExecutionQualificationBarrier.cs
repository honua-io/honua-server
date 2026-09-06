// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.ControlPlane;

/// <summary>
/// Opt-in, filesystem-backed barriers used by the production-worker qualification lane.
/// The feature is inert unless <c>HONUA_GP_QUALIFICATION_BARRIER_ROOT</c> is set, so it
/// cannot change normal worker behavior. A barrier writes a readiness record and waits for
/// the qualification runner to release it or for the execution token to be cancelled.
/// </summary>
public static class ExecutionQualificationBarrier
{
    /// <summary>Environment variable naming the shared barrier directory.</summary>
    public const string RootEnvironmentVariable = "HONUA_GP_QUALIFICATION_BARRIER_ROOT";

    /// <summary>Environment variable enabling the deliberate cancellation-ignoring mode.</summary>
    public const string ExecutorModeEnvironmentVariable = "HONUA_GP_QUALIFICATION_EXECUTOR_MODE";

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Whether qualification barriers are enabled for this process.</summary>
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootEnvironmentVariable));

    /// <summary>
    /// Whether the qualification executor should ignore operator cancellation while retaining
    /// the independent timeout token. This is deliberately available only through an explicit
    /// opt-in environment value used by the live timeout lane.
    /// </summary>
    public static bool IgnoresOperatorCancellation
        => string.Equals(
            Environment.GetEnvironmentVariable(ExecutorModeEnvironmentVariable),
            "ignore-cancellation",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Current qualification operation identifier, if a worker execution is active.</summary>
    public static string? CurrentOperationId => Current.Value?.OperationId;

    /// <summary>Current qualification worker identifier, if a worker execution is active.</summary>
    public static string? CurrentWorkerId => Current.Value?.WorkerId;

    /// <summary>
    /// Enters the operation scope inherited by asynchronous native-worker calls.
    /// </summary>
    public static IDisposable Begin(string operationId, string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        var previous = Current.Value;
        Current.Value = new Scope(operationId, workerId);
        return new ScopeLease(previous);
    }

    /// <summary>
    /// Publishes one barrier's readiness record and waits for release or cancellation.
    /// </summary>
    public static async Task WaitAsync(
        string barrier,
        CancellationToken cancellationToken,
        int? childProcessId = null,
        bool ignoreCancellation = false)
    {
        if (!Enabled || Current.Value is not { } scope)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(barrier);
        var root = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var operationDirectory = Path.Join(root, Safe(scope.OperationId));
        Directory.CreateDirectory(operationDirectory);
        var readyPath = Path.Join(operationDirectory, $"{Safe(barrier)}.ready.json");
        var releasePath = Path.Join(operationDirectory, $"{Safe(barrier)}.release");
        var observedPath = Path.Join(operationDirectory, $"{Safe(barrier)}.signal-observed.json");

        WriteJsonAtomically(
            readyPath,
            new
            {
                operationId = scope.OperationId,
                workerId = scope.WorkerId,
                barrier,
                readyAt = DateTimeOffset.UtcNow,
                workerProcessId = Environment.ProcessId,
                childProcessId,
                executorIgnoresCancellation = ignoreCancellation,
            });

        if (ignoreCancellation)
        {
            cancellationToken = CancellationToken.None;
        }

        using var registration = cancellationToken.Register(
            static state =>
            {
                var signal = (SignalObservation)state!;
                WriteJsonAtomically(
                    signal.Path,
                    new
                    {
                        operationId = signal.OperationId,
                        workerId = signal.WorkerId,
                        barrier = signal.Barrier,
                        observedAt = DateTimeOffset.UtcNow,
                        workerProcessId = Environment.ProcessId,
                        childProcessId = signal.ChildProcessId,
                        tokenCancelled = true,
                    });
            },
            new SignalObservation(
                observedPath,
                scope.OperationId,
                scope.WorkerId,
                barrier,
                childProcessId));

        while (!File.Exists(releasePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }

        WriteJsonAtomically(
            Path.Join(operationDirectory, $"{Safe(barrier)}.released.json"),
            new
            {
                operationId = scope.OperationId,
                workerId = scope.WorkerId,
                barrier,
                releasedAt = DateTimeOffset.UtcNow,
                workerProcessId = Environment.ProcessId,
                childProcessId,
            });
    }

    private static string Safe(string value)
        => value.Replace("/", "_", StringComparison.Ordinal)
            .Replace("\\", "_", StringComparison.Ordinal)
            .Replace("..", "_", StringComparison.Ordinal);

    private static void WriteJsonAtomically(string path, object value)
    {
        try
        {
            var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            // A qualification receipt is diagnostic evidence; never let a full disk or
            // concurrent cleanup change the production execution outcome.
        }
        catch (UnauthorizedAccessException)
        {
            // Same fail-open rule as the optional barrier itself.
        }
    }

    private sealed record Scope(string OperationId, string WorkerId);

    private sealed class ScopeLease(Scope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    private sealed record SignalObservation(
        string Path,
        string OperationId,
        string WorkerId,
        string Barrier,
        int? ChildProcessId);
}
