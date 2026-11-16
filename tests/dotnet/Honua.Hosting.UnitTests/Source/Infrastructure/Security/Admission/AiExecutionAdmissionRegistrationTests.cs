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
    [UnitTheory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("postgres")]
    public void AddAiExecutionAdmission_RegisteredAuthority_UsesDirectStore(string? backend)
    {
        var store = Substitute.For<IAiExecutionAdmissionStore>();
        var services = new ServiceCollection();
        services.AddScoped<IAiExecutionAdmissionStore>(_ => store);
        services.AddAiExecutionAdmission(Configuration(backend));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>().Should().BeSameAs(store);
        services.Should().NotContain(descriptor => descriptor.IsKeyedService &&
            descriptor.ServiceType == typeof(IAiExecutionAdmissionStore));
    }

    [UnitTheory]
    [InlineData("memory")]
    [InlineData("MEMORY")]
    [InlineData("unknown")]
    [InlineData("")]
    public void AddAiExecutionAdmission_RetiredOrInvalidAuthority_RejectsConfiguration(string backend)
    {
        var services = new ServiceCollection();

        var register = () => services.AddAiExecutionAdmission(Configuration(backend));

        register.Should().Throw<InvalidOperationException>();
    }

    [UnitTest]
    public async Task AcquireAsync_MissingAuthority_DeniesInvocation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiExecutionAdmission(Configuration(null));
        using var provider = services.BuildServiceProvider();
        var admission = provider.GetRequiredService<IAiExecutionAdmission>();
        var context = AiExecutionContext.Create("deployment", "test:actor", "studio-chat");

        await FluentActions.Awaiting(() => admission.AcquireAsync(context, default))
            .Should().ThrowAsync<AiAdmissionException>()
            .Where(exception => exception.Code == "unavailable");
    }

    private static IConfiguration Configuration(string? backend) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiExecutionAdmission:DeploymentId"] = "deployment",
            ["AiExecutionAdmission:Backend"] = backend
        }).Build();
}
