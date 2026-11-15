// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.Infrastructure.Security.Admission;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Security.Admission;

public sealed class AiExecutionAdmissionRegistrationTests
{
    [UnitTest]
    public void Backend_AutoOrPostgres_WithPostgresStore_SelectsPostgres()
    {
        foreach (var backend in new[] { "auto", "postgres", "Postgres" })
        {
            var postgres = Substitute.For<IAiExecutionAdmissionStore>();
            using var services = Build(backend, postgres);
            using var scope = services.CreateScope();

            scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>().Should().BeSameAs(postgres, backend);
        }
    }

    [UnitTest]
    public async Task Backend_RetiredRedisOrUnknown_DeniesEvenWithPostgresStore()
    {
        foreach (var backend in new[] { "redis", "memory", "" })
        {
            var postgres = Substitute.For<IAiExecutionAdmissionStore>();
            using var services = Build(backend, postgres);
            using var scope = services.CreateScope();

            var store = scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>();
            store.Should().NotBeSameAs(postgres, backend);
            (await store.TryAcquireAsync(null!, Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("unavailable", backend);
        }
    }

    [UnitTest]
    public async Task Backend_Auto_WithoutPostgresStore_Denies()
    {
        using var services = Build("auto", postgres: null);
        using var scope = services.CreateScope();

        var decision = await scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>()
            .TryAcquireAsync(null!, Guid.NewGuid(), CancellationToken.None);
        decision.Code.Should().Be("unavailable");
    }

    private static ServiceProvider Build(string backend, IAiExecutionAdmissionStore? postgres)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (postgres is not null)
        {
            services.AddKeyedScoped("postgres", (_, _) => postgres);
        }

        services.AddAiExecutionAdmission(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiExecutionAdmission:DeploymentId"] = "deployment",
            ["AiExecutionAdmission:Backend"] = backend,
        }).Build());
        return services.BuildServiceProvider();
    }
}
