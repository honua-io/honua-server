// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure;

[Protocol(Protocols.TestQuality)]
public sealed class ConfigurationServiceExtensionsTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AddStandardConfiguration_DoesNotRegisterSecretResolutionForSecretProviderOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddLogging();

        services.AddStandardConfiguration(configuration);

        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType == typeof(IPostConfigureOptions<SecretProviderOptions>));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        provider.GetRequiredService<IConnectionSecretResolver>()
            .GetSupportedProviders()
            .Should()
            .Contain(["env", "aws", "azure", "null"]);
        provider.GetRequiredService<IOptions<SecretProviderOptions>>().Value.Should().NotBeNull();
        provider.GetRequiredService<IOptions<SecureConfigurationOptions>>().Value.Should().NotBeNull();
        provider.GetRequiredService<ISecretProvider>().Should().NotBeNull();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Honua.Server.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
