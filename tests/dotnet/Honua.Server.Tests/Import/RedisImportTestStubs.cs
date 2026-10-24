// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

internal static class RedisImportTestStubs
{
    public static void ConfigureDurableProgressTransactions(IDatabase database)
    {
        var transaction = Substitute.For<ITransaction>();
        database.CreateTransaction().Returns(transaction);

        transaction.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(),
                Arg.Any<When>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        transaction.SetAddAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        transaction.KeyDeleteAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        transaction.SetRemoveAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        transaction.ExecuteAsync(Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
    }
}
