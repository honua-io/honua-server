// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Startup;

// Resolved secret references must survive the security-configuration source reordering that runs
// before the host is built.
public sealed class StartupConfigurationHelpersTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void FinalSourceOrdering_ResolvesSecurityOverrideInsteadOfBaseSecret()
    {
        var directory = Path.Join(Path.GetTempPath(), "honua-security-precedence-" + Guid.NewGuid().ToString("N"));
        var baseName = "HONUA_BASE_SECRET_" + Guid.NewGuid().ToString("N");
        var overrideName = "HONUA_OVERRIDE_SECRET_" + Guid.NewGuid().ToString("N");
        var expected = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable(baseName, Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(overrideName, expected);
            foreach (var (file, name) in new[] { ("appsettings.json", baseName), ("appsettings.Security.json", overrideName) })
            {
                File.WriteAllText(Path.Join(directory, file),
                    $$"""{"HONUA_ADMIN_PASSWORD":"env:{{name}}","Security:ConnectionEncryption:MasterKey":"env:{{name}}","ConnectionStrings:DefaultConnection":"env:{{name}}"}""");
            }
            File.WriteAllText(Path.Join(directory, "appsettings.Production.json"), "{}");
            using var configuration = new ConfigurationManager();
            configuration.SetBasePath(directory);
            configuration.AddJsonFile("appsettings.json");
            configuration.AddJsonFile("appsettings.Production.json");
            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(Environments.Production);

            StartupConfigurationHelpers.AddSecurityConfiguration(configuration, environment);
            StartupConfigurationHelpers.ResolveEnvironmentSecretReferences(configuration);

            foreach (var key in new[] { "HONUA_ADMIN_PASSWORD", "Security:ConnectionEncryption:MasterKey", "ConnectionStrings:DefaultConnection" })
            {
                string.Equals(configuration[key], expected, StringComparison.Ordinal).Should().BeTrue(key);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(baseName, null);
            Environment.SetEnvironmentVariable(overrideName, null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("ConnectionStrings:DefaultConnection", false)]
    [InlineData("ConnectionStrings:DefaultConnection", true)]
    [InlineData("Security:ConnectionEncryption:MasterKey", false)]
    [InlineData("Security:ConnectionEncryption:MasterKey", true)]
    public void SecuritySourceReordering_PreservesResolvedEnvironmentReferences(string key, bool includeSecurityFile)
    {
        var directory = Path.Join(Path.GetTempPath(), "honua-secret-order-" + Guid.NewGuid().ToString("N"));
        var prefix = "HONUA_SECRET_ORDER_" + Guid.NewGuid().ToString("N") + "_";
        var referenceName = prefix + "VALUE";
        var configurationName = prefix + key.Replace(":", "__", StringComparison.Ordinal);
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Join(directory, "appsettings.json"), "{}");
            File.WriteAllText(Path.Join(directory, "appsettings.Production.json"), "{}");
            if (includeSecurityFile)
            {
                File.WriteAllText(Path.Join(directory, "appsettings.Security.json"), "{}");
            }
            var resolved = "localhost:6379,password=" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(referenceName, resolved);
            Environment.SetEnvironmentVariable(configurationName, "env:" + referenceName);
            using var configuration = new ConfigurationManager();
            configuration.SetBasePath(directory);
            configuration.AddJsonFile("appsettings.json");
            configuration.AddJsonFile("appsettings.Production.json");
            configuration.AddEnvironmentVariables(prefix);
            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(Environments.Production);

            StartupConfigurationHelpers.ResolveEnvironmentSecretReferences(configuration);
            StartupConfigurationHelpers.AddSecurityConfiguration(configuration, environment);

            // Boolean assertion keeps generated credentials out of failure output.
            string.Equals(configuration[key], resolved, StringComparison.Ordinal).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(referenceName, null);
            Environment.SetEnvironmentVariable(configurationName, null);
            Directory.Delete(directory, recursive: true);
        }
    }
}
