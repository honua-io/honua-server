// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Identity;
using Honua.Server.Features.Identity.Scim;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Identity;

public sealed class ScimProvisioningOptionsTests
{
    [UnitTest]
    public void EnabledProvisioning_WithoutOidcIssuer_FailsValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Scim:BearerToken"] = "provisioning-secret",
        });

        var act = () => provider.GetRequiredService<IOptions<ScimProvisioningOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Scim:OidcIssuer");
    }

    [UnitTest]
    public void EnabledProvisioning_WithAbsoluteHttpsIssuer_SucceedsValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Scim:BearerToken"] = "provisioning-secret",
            ["Scim:OidcIssuer"] = "https://issuer.example.com/tenant",
        });

        provider.GetRequiredService<IOptions<ScimProvisioningOptions>>()
            .Value.OidcIssuer.Should().Be("https://issuer.example.com/tenant");
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddEnterpriseIdentity(configuration);
        return services.BuildServiceProvider();
    }
}
