// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Security;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class BootstrapSecuritySecretResolutionTests
{
    private const string AdminKey = "HONUA_ADMIN_PASSWORD";
    private const string MasterKey = "Security:ConnectionEncryption:MasterKey";

    [UnitTest]
    public async Task ResolveSecuritySecretReferences_PreservesRefreshableAdminReferenceAndSnapshotsMasterKey()
    {
        const string adminReference = "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:123456789012:secret:admin";
        const string masterKeyReference = "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:123456789012:secret:master";
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [AdminKey] = adminReference,
            [MasterKey] = masterKeyReference
        });
        var resolver = new StubSecretResolver(new Dictionary<string, string>
        {
            [adminReference] = "Production-Admin-Aa1!Resolved",
            [masterKeyReference] = "Production-Master-Key-Aa1!Resolved-00000000"
        });

        await StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration,
            resolver,
            [AdminKey, MasterKey],
            isProduction: true);

        configuration[AdminKey].Should().Be(adminReference);
        configuration[MasterKey].Should().Be("Production-Master-Key-Aa1!Resolved-00000000");
        resolver.ResolvedReferences.Should().BeEquivalentTo([adminReference, masterKeyReference]);
    }

    [UnitTest]
    public async Task ResolveSecuritySecretReferences_LeavesPlainValuesUntouched()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [AdminKey] = "Production-Admin-Aa1!Plain",
            [MasterKey] = "Production-Master-Key-Aa1!Plain-0000000000"
        });
        var resolver = new StubSecretResolver(new Dictionary<string, string>());

        await StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration,
            resolver,
            [AdminKey, MasterKey],
            isProduction: true);

        configuration[AdminKey].Should().Be("Production-Admin-Aa1!Plain");
        configuration[MasterKey].Should().Be("Production-Master-Key-Aa1!Plain-0000000000");
        resolver.ResolvedReferences.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ResolveSecuritySecretReferences_RejectsEmptyResolvedSecret()
    {
        const string reference = "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:123456789012:secret:admin";
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [AdminKey] = reference });
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = string.Empty });

        var action = () => StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration,
            resolver,
            [AdminKey],
            isProduction: true);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{AdminKey}*");
    }

    [UnitTest]
    public async Task ResolveSecuritySecretReferences_RejectsUnresolvableAwsReference()
    {
        const string reference = "aws:secretsmanager:missing-region-secret-name";
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [AdminKey] = reference });
        var resolver = new StubSecretResolver(new Dictionary<string, string>());

        var action = () => StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration,
            resolver,
            [AdminKey],
            isProduction: true);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{AdminKey}*invalid or cannot be resolved*");
        configuration[AdminKey].Should().Be(reference);
    }

    [UnitTest]
    public async Task ResolveSecuritySecretReferences_RejectsWeakResolvedAdminPasswordInProduction()
    {
        const string reference = "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:123456789012:secret:admin";
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [AdminKey] = reference });
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = "weak-password" });

        var action = () => StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration,
            resolver,
            [AdminKey],
            isProduction: true);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Admin password must be at least 16 characters in production environment");
        configuration[AdminKey].Should().Be(reference);
    }

    [UnitTest]
    public async Task AdminPortalCredentialVerifier_RejectsWeakRotatedProductionPassword()
    {
        const string reference = "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:123456789012:secret:admin";
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = "weak-password" });
        var verifier = BuildPortalVerifier(reference, resolver);

        var result = await verifier.VerifyAsync("admin", "weak-password", CancellationToken.None);

        result.Should().BeNull();
    }

    [UnitTest]
    public async Task AdminPortalCredentialVerifier_AcceptsValidRotatedProductionPassword()
    {
        const string reference = "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:123456789012:secret:admin";
        const string rotatedPassword = "Rotated-Production-Admin-Aa1!";
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = rotatedPassword });
        var verifier = BuildPortalVerifier(reference, resolver);

        var result = await verifier.VerifyAsync("admin", rotatedPassword, CancellationToken.None);

        result.Should().NotBeNull();
    }

    [UnitTest]
    public async Task AdminPortalCredentialVerifier_FailsClosedWhenSecretRefreshThrows()
    {
        const string reference = "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:123456789012:secret:admin";
        var verifier = BuildPortalVerifier(reference, new ThrowingSecretResolver());

        var result = await verifier.VerifyAsync("admin", "any-password", CancellationToken.None);

        result.Should().BeNull();
    }

    private static AdminPortalCredentialVerifier BuildPortalVerifier(
        string configuredPassword,
        IConnectionSecretResolver resolver) =>
        new(
            Options.Create(new ApiKeyAuthenticationOptions
            {
                AdminPassword = configuredPassword,
                EnvironmentName = "Production"
            }),
            resolver);

    private static ConfigurationManager BuildConfiguration(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        return configuration;
    }

    private sealed class StubSecretResolver(IReadOnlyDictionary<string, string> values)
        : IConnectionSecretResolver
    {
        public string ProviderName => "aws";
        public List<string> ResolvedReferences { get; } = [];

        public bool CanResolve(string secretKey) => values.ContainsKey(secretKey);

        public Task<string?> ResolveSecretAsync(
            string secretKey,
            CancellationToken cancellationToken = default)
        {
            ResolvedReferences.Add(secretKey);
            values.TryGetValue(secretKey, out var value);
            return Task.FromResult<string?>(value);
        }

        public async Task<string> ResolveConnectionStringAsync(
            string connectionStringTemplate,
            CancellationToken cancellationToken = default) =>
            await ResolveSecretAsync(connectionStringTemplate, cancellationToken).ConfigureAwait(false)
                ?? connectionStringTemplate;
    }

    private sealed class ThrowingSecretResolver : IConnectionSecretResolver
    {
        public string ProviderName => "throwing-test";

        public bool CanResolve(string secretKey) => true;

        public Task<string?> ResolveSecretAsync(
            string secretKey,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string?>(new InvalidOperationException("secret provider unavailable"));

        public Task<string> ResolveConnectionStringAsync(
            string connectionStringTemplate,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new InvalidOperationException("secret provider unavailable"));
    }
}
