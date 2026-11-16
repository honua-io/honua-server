// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Core.Features.ControlPlane;

public static class ExecutionQualificationBarrier
{
    public const string RootEnvironmentVariable = "HONUA_GP_QUALIFICATION_BARRIER_ROOT";

    public const string PauseAtEnvironmentVariable = "HONUA_GP_QUALIFICATION_PAUSE_AT";

    public const string ExecutorModeEnvironmentVariable = "HONUA_GP_QUALIFICATION_EXECUTOR_MODE";

    private static readonly AsyncLocal<Scope?> Current = new();

    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootEnvironmentVariable));

    public static bool IgnoresOperatorCancellation
        => string.Equals(
            Environment.GetEnvironmentVariable(ExecutorModeEnvironmentVariable),
            "ignore-cancellation",
            StringComparison.OrdinalIgnoreCase);

    public static string? CurrentOperationId => Current.Value?.OperationId;

    public static string? CurrentWorkerId => Current.Value?.WorkerId;

    public static IDisposable Begin(string operationId, string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        var previous = Current.Value;
        Current.Value = new Scope(operationId, workerId);
        return new ScopeLease(previous);
    }

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
        var pauseAt = Environment.GetEnvironmentVariable(PauseAtEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(pauseAt) && !string.Equals(pauseAt, barrier, StringComparison.Ordinal))
        {
            return;
        }

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
            ExecutionQualificationReceiptJsonContext.Default.QualificationReadyReceipt,
            new QualificationReadyReceipt(
                scope.OperationId,
                scope.WorkerId,
                barrier,
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                childProcessId,
                ignoreCancellation));

        if (ignoreCancellation)
        {
            cancellationToken = CancellationToken.None;
        }

        try
        {
            while (!File.Exists(releasePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The delay's cancellation callback may complete this method before an
            // earlier token callback runs. Record the observation in this method's
            // cancellation path so a canceled wait cannot outrun its receipt.
            WriteJsonAtomically(
                observedPath,
                ExecutionQualificationReceiptJsonContext.Default.QualificationSignalObservedReceipt,
                new QualificationSignalObservedReceipt(
                    scope.OperationId,
                    scope.WorkerId,
                    barrier,
                    DateTimeOffset.UtcNow,
                    Environment.ProcessId,
                    childProcessId,
                    TokenCancelled: true));
            throw;
        }

        WriteJsonAtomically(
            Path.Join(operationDirectory, $"{Safe(barrier)}.released.json"),
            ExecutionQualificationReceiptJsonContext.Default.QualificationReleasedReceipt,
            new QualificationReleasedReceipt(
                scope.OperationId,
                scope.WorkerId,
                barrier,
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                childProcessId));
    }

    private static string Safe(string value)
        => value.Replace("/", "_", StringComparison.Ordinal)
            .Replace("\\", "_", StringComparison.Ordinal)
            .Replace("..", "_", StringComparison.Ordinal);

    private static void WriteJsonAtomically<T>(string path, JsonTypeInfo<T> typeInfo, T value)
    {
        try
        {
            var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, typeInfo));
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
}
