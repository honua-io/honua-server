// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Postgres.Features.Infrastructure.Monitoring;

internal static class DbConnectionTracking
{
    public static void Track(DbConnection connection, IActiveDbConnectionTracker? tracker)
    {
        if (tracker == null)
        {
            return;
        }

        tracker.Increment();

        var decremented = 0;

        void OnStateChange(object? sender, StateChangeEventArgs args)
        {
            if (args.CurrentState is ConnectionState.Closed or ConnectionState.Broken)
            {
                if (Interlocked.Exchange(ref decremented, 1) == 0)
                {
                    tracker.Decrement();
                    connection.StateChange -= OnStateChange;
                }
            }
        }

        connection.StateChange += OnStateChange;

        if (connection.State is ConnectionState.Closed or ConnectionState.Broken)
        {
            if (Interlocked.Exchange(ref decremented, 1) == 0)
            {
                tracker.Decrement();
                connection.StateChange -= OnStateChange;
            }
        }
    }
}
