// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.EmbedGovernance;
using Honua.Core.Features.EmbedGovernance.Abstractions;
using Honua.Server.Features.Console.Services;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Console;

internal static class ConsoleStateTestHost
{
    // Test hosts share one database, and these endpoint tests assert exact totals and fixed ids,
    // so each host keeps its own Console state. The shared stores have their own tests.
    public static WebAppFixture UseProcessLocalConsoleState(this WebAppFixture fixture, TimeProvider? clock = null)
    {
        var time = clock ?? TimeProvider.System;
        return fixture
            .ReplaceService<IAuthoredConsoleContent>(new InMemoryConsoleContentStore(time))
            .ReplaceService<IConsoleShareStore>(new InMemoryConsoleShareStore(time))
            .ReplaceService<IConsoleOpenDataStore>(new InMemoryConsoleOpenDataStore(time))
            .ReplaceService<IEmbedKeyStore>(new InMemoryEmbedKeyStore(time))
            .ReplaceService(ConsolePersistenceNotice.InMemory);
    }
}
