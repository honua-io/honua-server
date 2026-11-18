// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Db.Postgres.Tests.Features.Security;

[Collection("Database")]
public sealed class PostgresAiAdmissionRegistrationTests(PostgresFixture fixture)
{
    // The spend notice reconciler resolves its store from the real registrations, so a
    // missing or mis-keyed registration would leave every 80% notice pending.
    [IntegrationTest]
    public void RegisteredServices_ResolveAdmissionAndSpendNoticeStoresToOneScopedStore()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString,
            ["Security:ConnectionEncryption:MasterKey"] = "admission-registration-test-master-key-0123456789",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        Honua.Db.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(
            services, configuration, TestCoreSchemaMigrations.Manifest);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var notices = scope.ServiceProvider.GetRequiredService<IAiMonthlyActorNoticeStore>();

        var admission = scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>();
        notices.Should().BeSameAs(admission);
        scope.ServiceProvider.GetRequiredService<IAiIncidentNoticeStore>().Should().BeSameAs(admission);
    }
}
