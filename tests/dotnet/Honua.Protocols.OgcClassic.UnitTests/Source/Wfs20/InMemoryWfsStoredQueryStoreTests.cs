// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed class InMemoryWfsStoredQueryStoreTests
{
    [UnitTest]
    public async Task TryCreateAsync_CanceledRequest_DoesNotCreateQuery()
    {
        var store = new InMemoryWfsStoredQueryStore();
        var entry = new WfsStoredQueryEntry(new WfsStoredQueryKey("scope", "query"), "{}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> create = () => store.TryCreateAsync(entry, cancellation.Token);

        await create.Should().ThrowAsync<OperationCanceledException>();
        (await store.GetAsync(entry.Key, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task DeleteAsync_CanceledRequest_PreservesQuery()
    {
        var store = new InMemoryWfsStoredQueryStore();
        var entry = new WfsStoredQueryEntry(new WfsStoredQueryKey("scope", "query"), "{}");
        await store.TryCreateAsync(entry, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> delete = () => store.DeleteAsync(entry.Key, cancellation.Token);

        await delete.Should().ThrowAsync<OperationCanceledException>();
        (await store.GetAsync(entry.Key, CancellationToken.None)).Should().Be(entry.Definition);
    }
}
