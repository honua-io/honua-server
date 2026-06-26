// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.Ogc.Classic;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Contract tests for <see cref="IWfsStoredQueryStore"/> using the in-memory
/// implementation, plus serialization round-trip tests for the Redis payload model
/// (no live Redis required — <see cref="RedisWfsStoredQueryStore"/> shares the same
/// source-generated JSON contract).
/// </summary>
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsStoredQueryStoreTests
{
    private static WfsStoredQueryDefinition CreateDefinition(
        string id,
        string? title = "Sample title",
        string? abstractText = "Sample abstract",
        string returnFeatureTypes = "honua:Layer1",
        string? queryTypeNames = "honua:Layer1",
        string? filterXml = "<fes:Filter><fes:ResourceId rid=\"${id}\" /></fes:Filter>",
        IReadOnlyList<WfsStoredQueryParameter>? parameters = null)
        => new(
            id,
            title,
            abstractText,
            returnFeatureTypes,
            "urn:ogc:def:queryLanguage:OGC-WFS::WFSQueryExpression",
            queryTypeNames,
            filterXml,
            parameters ?? [new WfsStoredQueryParameter("id", "xsd:string")]);

    [UnitTest]
    public async Task TryCreateAsync_NewId_ReturnsCreatedAndIsRetrievable()
    {
        var store = new InMemoryWfsStoredQueryStore();
        var definition = CreateDefinition("urn:example:query:one");

        var status = await store.TryCreateAsync(definition);

        status.Should().Be(WfsStoredQueryCreateStatus.Created);
        (await store.GetAsync("urn:example:query:one")).Should().Be(definition);
    }

    [UnitTest]
    public async Task TryCreateAsync_DuplicateIdDifferentCase_ReturnsDuplicateId()
    {
        var store = new InMemoryWfsStoredQueryStore();
        await store.TryCreateAsync(CreateDefinition("urn:Example:Query:One"));

        var status = await store.TryCreateAsync(CreateDefinition("URN:EXAMPLE:QUERY:ONE"));

        status.Should().Be(WfsStoredQueryCreateStatus.DuplicateId);
        (await store.CountAsync()).Should().Be(1);
    }

    [UnitTest]
    public async Task TryCreateAsync_AtCapacity_ReturnsCapacityExceeded()
    {
        var store = new InMemoryWfsStoredQueryStore();
        for (var i = 0; i < IWfsStoredQueryStore.MaxManagedStoredQueryCount; i++)
        {
            (await store.TryCreateAsync(CreateDefinition($"urn:example:query:{i}")))
                .Should().Be(WfsStoredQueryCreateStatus.Created);
        }

        var status = await store.TryCreateAsync(CreateDefinition("urn:example:query:overflow"));

        status.Should().Be(WfsStoredQueryCreateStatus.CapacityExceeded);
        (await store.GetAsync("urn:example:query:overflow")).Should().BeNull();
        (await store.CountAsync()).Should().Be(IWfsStoredQueryStore.MaxManagedStoredQueryCount);
    }

    [UnitTest]
    public async Task GetAsync_MissingId_ReturnsNull()
    {
        var store = new InMemoryWfsStoredQueryStore();

        (await store.GetAsync("urn:example:query:missing")).Should().BeNull();
    }

    [UnitTest]
    public async Task ListAsync_ReturnsDefinitionsOrderedByIdOrdinal()
    {
        var store = new InMemoryWfsStoredQueryStore();
        await store.TryCreateAsync(CreateDefinition("urn:example:query:b"));
        await store.TryCreateAsync(CreateDefinition("urn:example:query:a"));
        await store.TryCreateAsync(CreateDefinition("urn:example:query:c"));

        var listed = await store.ListAsync();

        listed.Select(query => query.Id).Should().ContainInOrder(
            "urn:example:query:a",
            "urn:example:query:b",
            "urn:example:query:c");
    }

    [UnitTest]
    public async Task TryRemoveAsync_ExistingIdDifferentCase_RemovesEntry()
    {
        var store = new InMemoryWfsStoredQueryStore();
        await store.TryCreateAsync(CreateDefinition("urn:example:query:one"));

        var removed = await store.TryRemoveAsync("URN:EXAMPLE:QUERY:ONE");

        removed.Should().BeTrue();
        (await store.GetAsync("urn:example:query:one")).Should().BeNull();
        (await store.CountAsync()).Should().Be(0);
    }

    [UnitTest]
    public async Task TryRemoveAsync_MissingId_ReturnsFalse()
    {
        var store = new InMemoryWfsStoredQueryStore();

        (await store.TryRemoveAsync("urn:example:query:missing")).Should().BeFalse();
    }

    [UnitTest]
    public async Task TryCreateAsync_AfterRemove_AllowsReuseOfId()
    {
        var store = new InMemoryWfsStoredQueryStore();
        await store.TryCreateAsync(CreateDefinition("urn:example:query:one", title: "Original"));
        await store.TryRemoveAsync("urn:example:query:one");

        var status = await store.TryCreateAsync(CreateDefinition("urn:example:query:one", title: "Replacement"));

        status.Should().Be(WfsStoredQueryCreateStatus.Created);
        (await store.GetAsync("urn:example:query:one"))!.Title.Should().Be("Replacement");
    }

    [UnitTest]
    public void RedisPayload_RoundTrip_PreservesAllFields()
    {
        var definition = CreateDefinition(
            "urn:example:query:full",
            parameters:
            [
                new WfsStoredQueryParameter("id", "xsd:string"),
                new WfsStoredQueryParameter("depth", "xsd:int"),
            ]);

        var payload = JsonSerializer.Serialize(definition, OgcClassicJsonContext.Default.WfsStoredQueryDefinition);
        var roundTripped = JsonSerializer.Deserialize(payload, OgcClassicJsonContext.Default.WfsStoredQueryDefinition);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(definition.Id);
        roundTripped.Title.Should().Be(definition.Title);
        roundTripped.Abstract.Should().Be(definition.Abstract);
        roundTripped.ReturnFeatureTypes.Should().Be(definition.ReturnFeatureTypes);
        roundTripped.Language.Should().Be(definition.Language);
        roundTripped.QueryTypeNames.Should().Be(definition.QueryTypeNames);
        roundTripped.FilterXml.Should().Be(definition.FilterXml);
        roundTripped.Parameters.Should().BeEquivalentTo(definition.Parameters, options => options.WithStrictOrdering());
    }

    [UnitTest]
    public void RedisPayload_RoundTrip_PreservesNullOptionalFields()
    {
        var definition = CreateDefinition(
            "urn:example:query:minimal",
            title: null,
            abstractText: null,
            returnFeatureTypes: string.Empty,
            queryTypeNames: null,
            filterXml: null,
            parameters: []);

        var payload = JsonSerializer.Serialize(definition, OgcClassicJsonContext.Default.WfsStoredQueryDefinition);
        var roundTripped = JsonSerializer.Deserialize(payload, OgcClassicJsonContext.Default.WfsStoredQueryDefinition);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(definition.Id);
        roundTripped.Title.Should().BeNull();
        roundTripped.Abstract.Should().BeNull();
        roundTripped.ReturnFeatureTypes.Should().BeEmpty();
        roundTripped.QueryTypeNames.Should().BeNull();
        roundTripped.FilterXml.Should().BeNull();
        roundTripped.Parameters.Should().BeEmpty();
    }
}
