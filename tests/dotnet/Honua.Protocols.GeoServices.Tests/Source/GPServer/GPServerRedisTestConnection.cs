// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Connection string for the durable-runtime GPServer test hosts that share the
/// <see cref="RedisFixture"/> container.
/// </summary>
/// <remarks>
/// Trunk red at 5364049 (run 35066422546) and four earlier full-matrix reds of the
/// GeoServices GPServer and NAServer shard all logged StackExchange.Redis timeouts at the
/// client's 5 s default ("5369ms elapsed, timeout is 5000ms", response bytes already in
/// the pipe, thread-pool queue backed up) while four-vCPU runners booted several test
/// hosts at once. Each timeout surfaced as a different test failure: a 408 on submit or
/// on a status poll, or a claimed job the worker abandoned mid-claim. The tests assert
/// job semantics, not Redis latency, so the hosts they build wait out a runner stall
/// instead of failing the call that happened to be in flight.
/// </remarks>
internal static class GPServerRedisTestConnection
{
    private const string StallTolerantTimeouts = ",syncTimeout=30000,asyncTimeout=30000";

    public static string For(RedisFixture redis) => redis.ConnectionString + StallTolerantTimeouts;
}
