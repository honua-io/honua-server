// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Honua.CustomCode.Harness;

/// <summary>
/// Wires the process lifetime signals (Ctrl+C, SIGTERM, process exit) to one cooperative
/// <see cref="CancellationTokenSource"/>. The exit handler used to call <c>Cancel()</c> on
/// a source that <c>using</c> had already disposed, which threw an unhandled
/// <see cref="ObjectDisposedException"/> after every successful <c>local</c> run; every
/// path here tolerates a disposed source and unsubscribes on dispose.
/// </summary>
public static class HarnessLifetime
{
    /// <summary>Cancels the source unless it is already cancelled or disposed.</summary>
    /// <param name="source">The source to cancel.</param>
    /// <returns>True when a cancellation was requested by this call.</returns>
    public static bool TryCancel(CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            if (source.IsCancellationRequested)
            {
                return false;
            }

            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Subscribes Ctrl+C, SIGTERM (non-Windows) and process exit to <paramref name="source"/>.
    /// Dispose the registration before the source so a late exit event finds no subscriber.
    /// </summary>
    /// <param name="source">The cooperative cancellation source.</param>
    public static IDisposable Register(CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Registration(source);
    }

    private sealed class Registration : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private readonly ConsoleCancelEventHandler _cancelKey;
        private readonly EventHandler _exit;
        private readonly PosixSignalRegistration? _sigterm;
        private int _disposed;

        public Registration(CancellationTokenSource source)
        {
            _source = source;
            _cancelKey = (_, e) =>
            {
                e.Cancel = true;
                TryCancel(_source);
            };
            _exit = (_, _) => TryCancel(_source);
            Console.CancelKeyPress += _cancelKey;
            AppDomain.CurrentDomain.ProcessExit += _exit;
            _sigterm = OperatingSystem.IsWindows()
                ? null
                : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    ctx.Cancel = true;
                    TryCancel(_source);
                });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Console.CancelKeyPress -= _cancelKey;
            AppDomain.CurrentDomain.ProcessExit -= _exit;
            _sigterm?.Dispose();
        }
    }
}
