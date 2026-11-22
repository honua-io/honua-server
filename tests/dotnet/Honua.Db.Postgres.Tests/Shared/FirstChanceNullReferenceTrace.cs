// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Honua.Db.Postgres.Tests.Shared;

// A NullReferenceException that surfaces with only the test's own frame has lost where it was thrown.
// This keeps the call stack of each first-chance NullReferenceException in the process while the
// action runs (other parallel tests included) and attaches them to that failure.
internal static class FirstChanceNullReferenceTrace
{
    private const int MaxTraces = 20;

    public static async Task RunAsync(Func<Task> action)
    {
        var traces = new ConcurrentQueue<string>();
        void Record(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is NullReferenceException && traces.Count < MaxTraces)
            {
                traces.Enqueue(Environment.StackTrace);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += Record;
        try
        {
            await action();
        }
        catch (NullReferenceException exception)
        {
            var separator = $"{Environment.NewLine}---{Environment.NewLine}";
            throw new InvalidOperationException(
                $"NullReferenceException; first-chance call stacks:{separator}{string.Join(separator, traces)}",
                exception);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Record;
        }
    }
}
