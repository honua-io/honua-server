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
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Honua.Server.Tests.Infrastructure.Security;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class BootstrapSecuritySecretResolutionTests
{
    private const string AdminKey = "HONUA_ADMIN_PASSWORD";
    private const string MasterKey = "Security:ConnectionEncryption:MasterKey";
    private const string AuditChainKey = "AuditLog:ChainVerification:Key";
    private const string OidcSigningKey = "Oidc:TokenValidation:SymmetricSigningKey";

    [UnitTest]
    public void SecuritySecretReferenceKeys_IncludeTheStartupSigningKeys()
    {
        // Lambda carries the audit-chain key as an aws:secretsmanager: reference; without bootstrap
        // resolution AuditChainKeyMaterial would reject the reference text as non-base64.
        StartupConfigurationHelpers.SecuritySecretReferenceKeys
            .Should().BeEquivalentTo([AdminKey, MasterKey, AuditChainKey, OidcSigningKey]);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [Trait("Tier", "Fast")]
    public async Task ResolvedOidcKey_PreservesSignedTokenValidation(string control)
    {
        const string reference = "aws:secretsmanager:fixture/oidc-signing-key";
        const string signingKey = "Run-owned-signing-key-with-at-least-32-bytes";
        const string issuer = "https://terminal-journey.invalid";
        const string audience = "terminal-journey";
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [OidcSigningKey] = reference,
            ["Oidc:Enabled"] = "true",
            ["Oidc:Generic:Enabled"] = "true",
            ["Oidc:Generic:Authority"] = issuer,
            ["Oidc:Generic:ClientId"] = audience
        });
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = signingKey });

        await StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration, resolver, StartupConfigurationHelpers.SecuritySecretReferenceKeys, isProduction: true);

        var options = configuration.GetSection("Oidc").Get<OidcAuthenticationOptions>();
        options.Should().NotBeNull();
        var validation = OidcTokenValidationParametersFactory.Build(options!).Parameters;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            control == "signature" ? "Different-signing-key-with-at-least-32-bytes" : signingKey));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: control == "issuer" ? "https://untrusted.invalid" : issuer,
            audience: control == "audience" ? "another-audience" : audience,
            claims: [new Claim("sub", "fixture-author"), new Claim("tenant_id", "tenant-a")],
            notBefore: now.AddHours(-1),
            expires: control == "expired" ? now.AddMinutes(-30) : now.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var handler = new JwtSecurityTokenHandler();
        var encoded = handler.WriteToken(token);

        if (control == "valid")
        {
            var principal = handler.ValidateToken(encoded, validation, out _);
            principal.FindFirst("tenant_id")?.Value.Should().Be("tenant-a");
        }
        else
        {
            var validate = () => handler.ValidateToken(encoded, validation, out _);
            validate.Should().Throw<SecurityTokenException>();
        }

        configuration[OidcSigningKey].Should().Be(signingKey);
        resolver.ResolvedReferences.Should().Equal(reference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Existing-static-signing-key-with-32-bytes")]
    [Trait("Tier", "Fast")]
    public async Task ResolveSecuritySecretReferences_LeavesUnconfiguredOrLiteralOidcKeysUntouched(string? value)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [OidcSigningKey] = value });
        var resolver = new StubSecretResolver(new Dictionary<string, string>());

        await StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration, resolver, StartupConfigurationHelpers.SecuritySecretReferenceKeys, isProduction: true);

        configuration[OidcSigningKey].Should().Be(value);
        resolver.ResolvedReferences.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [Trait("Tier", "Fast")]
    public async Task ResolveSecuritySecretReferences_RejectsEmptyReferencedOidcKey(string value)
    {
        const string reference = "aws:secretsmanager:private-fixture-signing-key";
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [OidcSigningKey] = reference });
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = value });

        var action = () => StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration, resolver, StartupConfigurationHelpers.SecuritySecretReferenceKeys, isProduction: true);

        var failure = await action.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.ToString().Should().NotContain(reference);
        configuration[OidcSigningKey].Should().Be(reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public async Task ResolveSecuritySecretReferences_RefusesUnresolvableOidcKeyWithoutProviderDetails(bool throws)
    {
        const string reference = "aws:secretsmanager:private-fixture-signing-key";
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [OidcSigningKey] = reference });
        IConnectionSecretResolver resolver = throws
            ? new ThrowingSecretResolver()
            : new StubSecretResolver(new Dictionary<string, string>());

        var action = () => StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration, resolver, StartupConfigurationHelpers.SecuritySecretReferenceKeys, isProduction: true);

        var failure = await action.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.ToString().Should().NotContain(reference).And.NotContain("secret provider unavailable");
        configuration[OidcSigningKey].Should().Be(reference);
    }

    [UnitTest]
    public async Task ResolveSecuritySecretReferences_SnapshotsTheAuditChainKey()
    {
        const string reference = "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:123456789012:secret:audit-chain-AbCdEf";
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray());
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [AuditChainKey] = reference });
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = key });

        await StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration,
            resolver,
            StartupConfigurationHelpers.SecuritySecretReferenceKeys,
            isProduction: true);

        configuration[AuditChainKey].Should().Be(key);
        Honua.Core.Features.AuditLog.Abstractions.AuditChainKeyMaterial.Decode(configuration[AuditChainKey])
            .Length.Should().Be(32);
    }

    [Theory]
    [InlineData(AuditChainKey)]
    [InlineData(OidcSigningKey)]
    [Trait("Tier", "Fast")]
    public void ResolveEnvironmentSecretReferences_ResolvesAnEnvironmentSigningKey(string configurationKey)
    {
        const string variable = "HONUA_TEST_AUDIT_CHAIN_KEY_5F2C";
        var key = Convert.ToBase64String(new byte[32]);
        Environment.SetEnvironmentVariable(variable, key);
        try
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?> { [configurationKey] = $"env:{variable}" });

            StartupConfigurationHelpers.ResolveEnvironmentSecretReferences(configuration);

            configuration[configurationKey].Should().Be(key);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [Trait("Tier", "Fast")]
    public async Task ResolveEnvironmentSigningKey_RejectsMissingOrBlankValueWithoutReferenceDetails(
        string? value, bool securityEntryPoint)
    {
        var variable = $"HONUA_TEST_PRIVATE_OIDC_KEY_{Guid.NewGuid():N}";
        var reference = $"env:{variable}";
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?> { [OidcSigningKey] = reference });

            Func<Task> action = async () =>
            {
                if (securityEntryPoint)
                {
                    await StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(configuration, isProduction: true);
                }
                else
                {
                    StartupConfigurationHelpers.ResolveEnvironmentSecretReferences(configuration);
                }
            };

            var failure = await action.Should().ThrowAsync<InvalidOperationException>();
            failure.Which.ToString().Should().Contain(OidcSigningKey).And.NotContain(reference).And.NotContain(variable);
            failure.Which.InnerException.Should().BeNull();
            configuration[OidcSigningKey].Should().Be(reference);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

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
