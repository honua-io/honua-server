// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Db.Postgres.Features.Studio;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Db.Postgres.Tests.Features.Studio;

public sealed partial class PostgresStudioPackageStoreTests
{
    [IntegrationTest]
    public async Task PreviewPlan_ExistingDraft_DoesNotChangeGenerationOrAuditFields()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests) + "_Preview");
        try
        {
            await EnsureStudioTablesAsync(schema);
            var store = new PostgresStudioPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var created = await store.CreateDraftAsync(BuildDraft("1=1"));
            var before = (await store.GetDraftAsync(created.DraftId))!;
            var services = new ServiceCollection();
            services.AddSingleton<IStudioPackageStore>(store);
            services.AddStudioPackageLifecycle();
            using var provider = services.BuildServiceProvider();
            var service = provider.GetRequiredService<IStudioPackageLifecycleService>();

            var plan = await service.PreviewPlanAsync(before.DraftId, "previewer");
            var after = (await store.GetDraftAsync(before.DraftId))!;

            plan.Should().NotBeNull();
            plan!.Synchronous.Should().BeTrue();
            plan.RequiresJob.Should().BeFalse();
            plan.Validation.Status.Should().Be(StudioPackageValidationStatus.Valid);
            after.Generation.Should().Be(before.Generation);
            after.UpdatedBy.Should().Be(before.UpdatedBy);
            after.UpdatedAt.Should().Be(before.UpdatedAt);
            after.Validation.GeneratedAt.Should().Be(before.Validation.GeneratedAt);
            after.Envelope.Validation.Should().BeEquivalentTo(before.Envelope.Validation);
            (await service.PreviewPlanAsync(Guid.NewGuid(), "previewer")).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
