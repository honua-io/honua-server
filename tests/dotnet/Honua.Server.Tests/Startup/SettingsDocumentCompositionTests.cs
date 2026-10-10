// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Boots the real <c>Program</c> with <c>HONUA_SETTINGS_DOCUMENT</c> pointing at a stub Secrets
/// Manager and proves the document's settings reach the running host, that environment variables
/// and command-line settings still override it, and that a reference inside the document resolves
/// through the existing bootstrap security resolution.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class SettingsDocumentCompositionTests
{
    private const string DocumentReference = "aws:secretsmanager:honua/composition/settings";
    private const string AuditReference = "aws:secretsmanager:honua/composition/audit-chain-key";

    [IntegrationTest]
    public async Task Program_WithSettingsDocument_AppliesDocumentBelowEnvironmentAndCommandLine()
    {
        var section = "SettingsDocComposition" + Guid.NewGuid().ToString("N");
        var environmentVariable = $"{section}__FromEnvironment";
        var auditKey = Convert.ToBase64String(Enumerable.Range(100, 32).Select(static i => (byte)i).ToArray());
        var resolver = new SettingsDocumentConfigurationTests.StubSecretResolver(new Dictionary<string, string>
        {
            [DocumentReference] = $$"""
                {
                  "{{section}}": {
                    "FromDocument": "document",
                    "FromEnvironment": "document",
                    "FromCommandLine": "document"
                  },
                  "Cors": { "AllowedOrigins": ["https://{{section.ToLowerInvariant()}}.example.com"] },
                  "AuditLog__ChainVerification__Key": "{{AuditReference}}"
                }
                """,
            [AuditReference] = auditKey,
        });

        Environment.SetEnvironmentVariable(environmentVariable, "environment");
        try
        {
            using var stub = BootstrapSecretResolver.UseForTesting(resolver);
            await using var baseFactory = new TestWebApplicationFactory();
            await using var factory = baseFactory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting(SettingsDocumentConfiguration.EnvironmentVariableName, DocumentReference);
                builder.UseSetting($"{section}:FromCommandLine", "command-line");
            });

            var configuration = factory.Services.GetRequiredService<IConfiguration>();

            configuration[$"{section}:FromDocument"].Should().Be("document");
            configuration[$"{section}:FromEnvironment"].Should().Be("environment");
            configuration[$"{section}:FromCommandLine"].Should().Be("command-line");
            configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                .Should().Equal($"https://{section.ToLowerInvariant()}.example.com");
            configuration[StartupConfigurationHelpers.AuditChainKeyConfigurationKey].Should().Be(
                auditKey,
                "a reference inside the document resolves through the existing security-setting snapshot");
            resolver.ResolvedReferences.Should().Contain([DocumentReference, AuditReference]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, null);
        }
    }

    [IntegrationTest]
    public async Task Program_WithUnreadableSettingsDocument_RefusesToStart()
    {
        var resolver = new SettingsDocumentConfigurationTests.StubSecretResolver(new Dictionary<string, string>
        {
            [DocumentReference] = "[\"not\", \"an\", \"object\"]",
        });

        using var stub = BootstrapSecretResolver.UseForTesting(resolver);
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseSetting(SettingsDocumentConfiguration.EnvironmentVariableName, DocumentReference));

        var start = () => factory.Services;

        var thrown = start.Should().Throw<Exception>().Which;
        Unwrap(thrown).OfType<SettingsDocumentException>()
            .Should().Contain(ex => ex.Failure == SettingsDocumentFailure.NotObject);
    }

    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                {
                    yield return inner;
                }
            }
        }
    }
}
