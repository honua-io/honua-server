// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class LocalProcessPoolBatchComputeBackend
{
    private readonly record struct TerminalOutcome(
        ExecutionJobStatus Status,
        string? Message,
        DateTimeOffset RecordedAt);

    private readonly record struct LaunchContext(
        ExecutionJobRecord Job,
        string Executable,
        int ExecutionAttempt);

    private sealed class ProcessExecution(string operationId, string? scratchDirectory) : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<string> _tail = new();
        private readonly CancellationTokenSource _cts = new();
        private ExecutionJobStatus _status = ExecutionJobStatus.Running;
        private string? _message;
        private DateTimeOffset? _completedAt;
        private bool _observed;

        public string OperationId { get; } = operationId;

        public string? ScratchDirectory { get; } = scratchDirectory;

        public CancellationToken CancellationToken => _cts.Token;

        public void RequestCancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already completed and disposed.
            }
        }

        public void AppendTail(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_sync)
            {
                _tail.Enqueue(line);
                while (_tail.Count > MaxTailLines)
                {
                    _tail.Dequeue();
                }
            }
        }

        public void CompleteFromExit(int exitCode)
        {
            lock (_sync)
            {
                _status = exitCode == 0 ? ExecutionJobStatus.Succeeded : ExecutionJobStatus.Failed;
                _message = exitCode == 0
                    ? "Local process completed successfully."
                    : $"Local process exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.{FormatTail()}";
                _completedAt = DateTimeOffset.UtcNow;
            }

            DisposeToken();
        }

        public void CompleteCancelled()
        {
            lock (_sync)
            {
                _status = ExecutionJobStatus.Cancelled;
                _message = "Local process was cancelled.";
                _completedAt = DateTimeOffset.UtcNow;
            }

            DisposeToken();
        }

        public void CompleteFailed()
        {
            lock (_sync)
            {
                _status = ExecutionJobStatus.Failed;
                _message = "Local process monitoring failed.";
                _completedAt = DateTimeOffset.UtcNow;
            }

            DisposeToken();
        }

        public void MarkObserved()
        {
            lock (_sync)
            {
                _observed = true;
            }
        }

        public TerminalOutcome? TakeUnobservedTerminalOutcome()
        {
            lock (_sync)
            {
                if (_observed || !_status.IsTerminal())
                {
                    return null;
                }

                _observed = true;
                return new TerminalOutcome(_status, _message, DateTimeOffset.UtcNow);
            }
        }

        public bool IsExpired(DateTimeOffset now, TimeSpan retentionTtl)
        {
            lock (_sync)
            {
                return _status.IsTerminal()
                    && _completedAt is { } completedAt
                    && now - completedAt >= retentionTtl;
            }
        }

        public (ExecutionJobStatus Status, string? Message) Snapshot()
        {
            lock (_sync)
            {
                return (_status, _message);
            }
        }

        private string FormatTail()
        {
            if (_tail.Count == 0)
            {
                return string.Empty;
            }

            var tail = string.Join(" ", _tail);
            if (tail.Length > 500)
            {
                tail = tail[^500..];
            }

            return $" Output tail: {tail}";
        }

        private void DisposeToken()
        {
            try
            {
                _cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Defensive: guards a concurrent Cancel()/Dispose() race from the monitor
                // task and an explicit Dispose() call on this execution.
            }
        }

        public void Dispose() => DisposeToken();
    }
}
