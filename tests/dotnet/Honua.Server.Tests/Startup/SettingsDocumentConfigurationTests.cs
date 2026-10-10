// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Owner decision 6: one <c>aws:secretsmanager:</c> settings document expands into configuration
/// below the environment, so Lambda settings can leave the 4 KB function environment.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class SettingsDocumentConfigurationTests
{
    private const string Reference = "aws:secretsmanager:honua/prod/settings";
    private const string SecretSentinel = "Sentinel-Value-That-Must-Never-Be-Echoed-7f3a";

    [UnitTest]
    public void Parse_NestedObjects_MapToSectionKeys()
    {
        var data = SettingsDocumentConfiguration.Parse(
            Reference,
            """{"Cors":{"AllowCredentials":true},"Limits":{"Query":{"MaxRecordCount":5000}}}""");

        data.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Cors:AllowCredentials"] = "True",
            ["Limits:Query:MaxRecordCount"] = "5000",
        });
    }

    [UnitTest]
    public void Parse_FlatEnvironmentStyleKeys_MapToSectionKeys()
    {
        var data = SettingsDocumentConfiguration.Parse(
            Reference,
            """{"Cors__AllowedOrigins__0":"https://app.example.com","Geocoding:Providers:AmazonLocation:Enabled":"true","HONUA_SERVE_API_DOCS":false}""");

        data.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://app.example.com",
            ["Geocoding:Providers:AmazonLocation:Enabled"] = "true",
            ["HONUA_SERVE_API_DOCS"] = "False",
        });
    }

    [UnitTest]
    public void Parse_Arrays_UseAspNetIndexedKeys()
    {
        var data = SettingsDocumentConfiguration.Parse(
            Reference,
            """
            {
              "Cors": { "AllowedOrigins": ["https://a.example.com", "https://b.example.com"] },
              "Operations": { "Policy": { "Enabled": true, "Rules": [
                { "OperationId": "deploy.*", "Decision": "RequireApproval" },
                { "OperationId": "*", "Role": "viewer", "Decision": "Deny" }
              ] } },
              "Empty": [],
              "Unset": null
            }
            """);

        data.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://a.example.com",
            ["Cors:AllowedOrigins:1"] = "https://b.example.com",
            ["Operations:Policy:Enabled"] = "True",
            ["Operations:Policy:Rules:0:OperationId"] = "deploy.*",
            ["Operations:Policy:Rules:0:Decision"] = "RequireApproval",
            ["Operations:Policy:Rules:1:OperationId"] = "*",
            ["Operations:Policy:Rules:1:Role"] = "viewer",
            ["Operations:Policy:Rules:1:Decision"] = "Deny",
            ["Empty"] = null,
            ["Unset"] = null,
        });
    }

    [UnitTest]
    public void Parse_ArrayBindsLikeAppSettings()
    {
        var data = SettingsDocumentConfiguration.Parse(
            Reference,
            """{"Cors":{"AllowedOrigins":["https://a.example.com","https://b.example.com"]}}""");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();

        configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            .Should().Equal("https://a.example.com", "https://b.example.com");
    }

    [UnitTest]
    public void Parse_DuplicateKeyAcrossNestedAndFlatForms_Refuses()
    {
        var action = () => SettingsDocumentConfiguration.Parse(
            Reference,
            """{"Cors":{"AllowCredentials":true},"cors__allowcredentials":false}""");

        action.Should().Throw<SettingsDocumentException>()
            .Where(ex => ex.Failure == SettingsDocumentFailure.DuplicateKey && ex.DocumentReference == Reference)
            .WithMessage($"*{Reference}*");
    }

    [UnitTest]
    public void Parse_NonJson_RefusesWithoutEchoingContent()
    {
        var action = () => SettingsDocumentConfiguration.Parse(Reference, $"Password={SecretSentinel}");

        var failure = action.Should().Throw<SettingsDocumentException>().Which;
        failure.Failure.Should().Be(SettingsDocumentFailure.NotJson);
        failure.Message.Should().Contain(Reference).And.NotContain(SecretSentinel).And.NotContain("Password");
        failure.InnerException.Should().BeNull("a JsonException message can quote document bytes");
    }

    [UnitTest]
    public void Parse_TruncatedJson_RefusesWithoutEchoingContent()
    {
        var action = () => SettingsDocumentConfiguration.Parse(Reference, $$"""{"Key":"{{SecretSentinel}}" """);

        var failure = action.Should().Throw<SettingsDocumentException>().Which;
        failure.Failure.Should().Be(SettingsDocumentFailure.NotJson);
        failure.ToString().Should().NotContain(SecretSentinel);
    }

    [UnitTheory]
    [InlineData("[{\"Key\":\"value\"}]", "array")]
    [InlineData("\"just-a-string\"", "string")]
    [InlineData("42", "number")]
    public void Parse_NonObjectRoot_Refuses(string content, string kind)
    {
        var action = () => SettingsDocumentConfiguration.Parse(Reference, content);

        action.Should().Throw<SettingsDocumentException>()
            .Where(ex => ex.Failure == SettingsDocumentFailure.NotObject)
            .WithMessage($"*{Reference}*JSON {kind}*");
    }

    [UnitTest]
    public void Parse_LargerThan64Kilobytes_Refuses()
    {
        var content = $$"""{"Padding":"{{new string('x', SettingsDocumentConfiguration.MaxDocumentBytes)}}"}""";

        var action = () => SettingsDocumentConfiguration.Parse(Reference, content);

        action.Should().Throw<SettingsDocumentException>()
            .Where(ex => ex.Failure == SettingsDocumentFailure.TooLarge)
            .WithMessage($"*{Reference}*64 KB*");
    }

    [UnitTest]
    public void Parse_AtExactly64Kilobytes_IsAccepted()
    {
        var overhead = """{"Padding":""}""".Length;
        var padding = SettingsDocumentConfiguration.MaxDocumentBytes - overhead;
        var content = $$"""{"Padding":"{{new string('x', padding)}}"}""";
        System.Text.Encoding.UTF8.GetByteCount(content).Should().Be(SettingsDocumentConfiguration.MaxDocumentBytes);

        SettingsDocumentConfiguration.Parse(Reference, content).Should().ContainKey("Padding");
    }

    [UnitTheory]
    [InlineData("""{"HONUA_SETTINGS_DOCUMENT":"aws:secretsmanager:other"}""")]
    [InlineData("""{"Settings":{"Document":"aws:secretsmanager:other"}}""")]
    [InlineData("""{"Settings__Document":"aws:secretsmanager:other"}""")]
    public void Parse_DocumentNamingAnotherDocument_Refuses(string content)
    {
        var action = () => SettingsDocumentConfiguration.Parse(Reference, content);

        action.Should().Throw<SettingsDocumentException>()
            .Where(ex => ex.Failure == SettingsDocumentFailure.ForbiddenKey);
    }

    [UnitTest]
    public async Task Add_WithoutReference_DoesNothing()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Other"] = "1" });
        var resolver = new StubSecretResolver(new Dictionary<string, string>());

        var loaded = await SettingsDocumentConfiguration.AddSettingsDocumentAsync(
            configuration, resolver, NullLogger.Instance);

        loaded.Should().BeNull();
        resolver.ResolvedReferences.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Add_PrecedenceIsAppSettingsThenDocumentThenEnvironmentThenCommandLine()
    {
        var section = "SettingsDocPrecedence" + Guid.NewGuid().ToString("N");
        var environmentVariable = $"{section}__FromEnvironment";
        Environment.SetEnvironmentVariable(environmentVariable, "environment");
        try
        {
            var configuration = new ConfigurationManager();
            // Stands in for appsettings*.json: lowest precedence.
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SettingsDocumentConfiguration.ConfigurationKey] = Reference,
                [$"{section}:FromAppSettings"] = "appsettings",
                [$"{section}:FromDocument"] = "appsettings",
                [$"{section}:FromEnvironment"] = "appsettings",
                [$"{section}:FromCommandLine"] = "appsettings",
            });
            configuration.AddEnvironmentVariables();
            configuration.AddCommandLine([$"--{section}:FromCommandLine=command-line"]);
            var resolver = new StubSecretResolver(new Dictionary<string, string>
            {
                [Reference] = $$"""
                    {"{{section}}": {
                      "FromDocument": "document",
                      "FromEnvironment": "document",
                      "FromCommandLine": "document"
                    }
                    }
                    """,
            });

            var loaded = await SettingsDocumentConfiguration.AddSettingsDocumentAsync(
                configuration, resolver, NullLogger.Instance);

            loaded.Should().Be(3);
            configuration[$"{section}:FromAppSettings"].Should().Be("appsettings");
            configuration[$"{section}:FromDocument"].Should().Be("document");
            configuration[$"{section}:FromEnvironment"].Should().Be("environment");
            configuration[$"{section}:FromCommandLine"].Should().Be("command-line");
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, null);
        }
    }

    [UnitTest]
    public async Task Add_LogsKeyCountAndReferenceButNoValues()
    {
        var configuration = Configure(SettingsDocumentConfiguration.EnvironmentVariableName, Reference);
        var resolver = new StubSecretResolver(new Dictionary<string, string>
        {
            [Reference] = $$"""{"A":"{{SecretSentinel}}","B":{"C":"{{SecretSentinel}}"} }""",
        });
        var logger = new CapturingLogger();

        await SettingsDocumentConfiguration.AddSettingsDocumentAsync(configuration, resolver, logger);

        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("2").And.Contain(Reference).And.NotContain(SecretSentinel);
        configuration["B:C"].Should().Be(SecretSentinel);
    }

    [UnitTest]
    public async Task Add_ProviderDescriptionNamesOnlyTheReference()
    {
        var configuration = Configure(SettingsDocumentConfiguration.EnvironmentVariableName, Reference);
        var resolver = new StubSecretResolver(new Dictionary<string, string>
        {
            [Reference] = $$"""{"A":"{{SecretSentinel}}"}""",
        });

        await SettingsDocumentConfiguration.AddSettingsDocumentAsync(configuration, resolver, NullLogger.Instance);

        var debugView = ((IConfigurationRoot)configuration).GetDebugView(_ => "[redacted]");
        debugView.Should().Contain($"SettingsDocument({Reference})").And.NotContain(SecretSentinel);
    }

    [UnitTest]
    public async Task Add_ResolverThrows_RefusesAsUnreadable()
    {
        var configuration = Configure(SettingsDocumentConfiguration.EnvironmentVariableName, Reference);

        var action = () => SettingsDocumentConfiguration.AddSettingsDocumentAsync(
            configuration, new ThrowingSecretResolver(), NullLogger.Instance);

        var failure = (await action.Should().ThrowAsync<SettingsDocumentException>()).Which;
        failure.Failure.Should().Be(SettingsDocumentFailure.Unreadable);
        failure.Message.Should().Contain(Reference).And.Contain("secretsmanager:GetSecretValue");
    }

    [UnitTest]
    public async Task Add_UnresolvableReference_RefusesAsUnreadable()
    {
        var configuration = Configure("Settings:Document", Reference);

        var action = () => SettingsDocumentConfiguration.AddSettingsDocumentAsync(
            configuration, new StubSecretResolver(new Dictionary<string, string>()), NullLogger.Instance);

        (await action.Should().ThrowAsync<SettingsDocumentException>()).Which
            .Failure.Should().Be(SettingsDocumentFailure.Unreadable);
    }

    [UnitTest]
    public async Task Add_EmptySecret_RefusesAsUnreadable()
    {
        var configuration = Configure(SettingsDocumentConfiguration.EnvironmentVariableName, Reference);

        var action = () => SettingsDocumentConfiguration.AddSettingsDocumentAsync(
            configuration,
            new StubSecretResolver(new Dictionary<string, string> { [Reference] = "  " }),
            NullLogger.Instance);

        (await action.Should().ThrowAsync<SettingsDocumentException>()).Which
            .Failure.Should().Be(SettingsDocumentFailure.Unreadable);
    }

    [UnitTheory]
    [InlineData("file:/etc/honua/settings.json")]
    [InlineData("s3://bucket/settings.json")]
    [InlineData("honua/prod/settings")]
    public async Task Add_UnsupportedScheme_Refuses(string reference)
    {
        var configuration = Configure(SettingsDocumentConfiguration.EnvironmentVariableName, reference);
        var resolver = new StubSecretResolver(new Dictionary<string, string>());

        var action = () => SettingsDocumentConfiguration.AddSettingsDocumentAsync(
            configuration, resolver, NullLogger.Instance);

        (await action.Should().ThrowAsync<SettingsDocumentException>()).Which
            .Failure.Should().Be(SettingsDocumentFailure.UnsupportedReference);
        resolver.ResolvedReferences.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Add_ConflictingSpellings_Refuses()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SettingsDocumentConfiguration.EnvironmentVariableName] = Reference,
            [SettingsDocumentConfiguration.ConfigurationKey] = "aws:secretsmanager:honua/other",
        });

        var action = () => SettingsDocumentConfiguration.AddSettingsDocumentAsync(
            configuration, new StubSecretResolver(new Dictionary<string, string>()), NullLogger.Instance);

        (await action.Should().ThrowAsync<SettingsDocumentException>()).Which
            .Failure.Should().Be(SettingsDocumentFailure.AmbiguousReference);
    }

    [UnitTest]
    public async Task ReferencesInsideTheDocument_ResolveThroughTheExistingSecurityResolution()
    {
        const string auditReference = "aws:secretsmanager:honua/prod/audit-chain-key";
        const string adminReference = "aws:secretsmanager:honua/prod/admin-password";
        var auditKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray());
        var configuration = Configure(SettingsDocumentConfiguration.EnvironmentVariableName, Reference);
        var resolver = new StubSecretResolver(new Dictionary<string, string>
        {
            [Reference] = $$"""
                {
                  "AuditLog": { "ChainVerification": { "Key": "{{auditReference}}" } },
                  "HONUA_ADMIN_PASSWORD": "{{adminReference}}"
                }
                """,
            [auditReference] = auditKey,
            [adminReference] = "Production-Admin-Aa1!Resolved",
        });

        await SettingsDocumentConfiguration.AddSettingsDocumentAsync(configuration, resolver, NullLogger.Instance);
        await StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration,
            resolver,
            StartupConfigurationHelpers.SecuritySecretReferenceKeys,
            isProduction: true);

        configuration[StartupConfigurationHelpers.AuditChainKeyConfigurationKey].Should().Be(auditKey);
        configuration["HONUA_ADMIN_PASSWORD"].Should().Be(adminReference, "the admin credential keeps its refreshable reference");
        resolver.ResolvedReferences.Should().Equal(Reference, adminReference, auditReference);
    }

    private static ConfigurationManager Configure(string key, string value)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
        return configuration;
    }

    internal sealed class StubSecretResolver(IReadOnlyDictionary<string, string> values) : IConnectionSecretResolver
    {
        public string ProviderName => "aws";

        public List<string> ResolvedReferences { get; } = [];

        public bool CanResolve(string secretKey) => values.ContainsKey(secretKey);

        public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
        {
            lock (ResolvedReferences)
            {
                ResolvedReferences.Add(secretKey);
            }

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
        public string ProviderName => "aws";

        public bool CanResolve(string secretKey) => true;

        public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default) =>
            Task.FromException<string?>(new InvalidOperationException("AWS Secrets Manager request failed with status code 400."));

        public Task<string> ResolveConnectionStringAsync(
            string connectionStringTemplate,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new InvalidOperationException("unavailable"));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
