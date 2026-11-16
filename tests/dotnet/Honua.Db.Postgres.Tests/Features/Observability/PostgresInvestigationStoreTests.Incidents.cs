// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Observability.Domain;
using Honua.Db.Postgres.Features.Observability;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Observability;

public sealed partial class PostgresInvestigationStoreTests
{
    [IntegrationTest]
    public async Task IncidentReferences_RoundTripCountAndRemoveAcrossStoreInstances()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresInvestigationStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresInvestigationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var now = DateTimeOffset.UtcNow;
            var created = await store.CreateAsync("Incident evidence", "alice", null, now);
            await store.AddLinkAsync(created.InvestigationId, InvestigationResourceKind.Release, "release", null, "alice", now);
            await store.AddPinAsync(created.InvestigationId, "alert:1", OperateEventKind.Alert, now, null, "alice", now);
            await store.AddLinkAsync(created.InvestigationId, InvestigationResourceKind.Incident, "incident:platform:one", "related", "alice", now);
            await store.AddPinAsync(created.InvestigationId, "incident:platform:one:v3", OperateEventKind.Incident, now.AddHours(-1), "evidence", "alice", now);
            await AssertIncidentReferencesAsync(schema, created.InvestigationId, now);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task AssertIncidentReferencesAsync(string schema, string investigationId, DateTimeOffset now)
    {
        var store = new PostgresInvestigationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
        var loaded = (await store.GetAsync(investigationId))!;
        loaded.Links.Should().HaveCount(2).And.OnlyHaveUniqueItems(link => link.LinkId);
        loaded.Pins.Should().HaveCount(2).And.OnlyHaveUniqueItems(pin => pin.PinId);
        var link = loaded.Links.Single(item => item.ResourceKind == InvestigationResourceKind.Incident);
        var pin = loaded.Pins.Single(item => item.EventKind == OperateEventKind.Incident);
        link.Note.Should().Be("related");
        pin.OccurredAt.Should().BeCloseTo(now.AddHours(-1), TimeSpan.FromMicroseconds(1));
        var page = await store.ListAsync(new InvestigationFilter());
        page.Items.Single().LinkCount.Should().Be(2);
        page.Items.Single().PinCount.Should().Be(2);
        await store.RemoveLinkAsync(investigationId, link.LinkId, now);
        var removed = (await store.RemovePinAsync(investigationId, pin.PinId, now))!;
        removed.Links.Should().ContainSingle(item => item.ResourceKind == InvestigationResourceKind.Release);
        removed.Pins.Should().ContainSingle(item => item.EventKind == OperateEventKind.Alert);
        page = await store.ListAsync(new InvestigationFilter());
        page.Items.Single().LinkCount.Should().Be(1);
        page.Items.Single().PinCount.Should().Be(1);
    }
}
