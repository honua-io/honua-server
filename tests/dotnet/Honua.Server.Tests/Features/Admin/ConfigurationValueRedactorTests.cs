// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit tests for the deny-by-default configuration value redaction that backs the
/// admin config-discovery endpoint (issue #2804). Verifies that credential-bearing
/// values are redacted regardless of property name and that only scalar value types
/// incapable of embedding secrets are exposed.
/// </summary>
public sealed class ConfigurationValueRedactorTests
{
    private sealed class SampleOptions
    {
        public string? ConnectionString { get; set; }

        public string? Endpoint { get; set; }

        public string? Username { get; set; }

        public string? DisplayName { get; set; }

        public bool Enabled { get; set; }

        public int MaxConnections { get; set; }

        public TimeSpan Timeout { get; set; }

        public SampleMode Mode { get; set; }

        public List<string> AllowedHosts { get; set; } = new();

        public SampleNested Nested { get; set; } = new();
    }

    private sealed class SampleNested
    {
        public string? Password { get; set; }
    }

    private enum SampleMode
    {
        Off,
        On,
    }

    private static PropertyInfo Property(string name) =>
        typeof(SampleOptions).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!;

    [UnitTest]
    public void Redact_ConnectionStringWithEmbeddedPassword_IsRedacted()
    {
        const string connectionString =
            "Host=db.internal;Port=5432;Database=honua;Username=app;Password=sup3r-s3cret-value";

        var redacted = ConfigurationValueRedactor.Redact(Property(nameof(SampleOptions.ConnectionString)), connectionString);

        redacted.Should().Be(ConfigurationValueRedactor.RedactedPlaceholder);
        redacted!.ToString().Should().NotContain("sup3r-s3cret-value");
        redacted.ToString().Should().NotContain("Password=");
    }

    [UnitTest]
    public void Redact_EndpointAndUsernameStrings_AreRedacted()
    {
        ConfigurationValueRedactor.Redact(Property(nameof(SampleOptions.Endpoint)), "https://svc.internal:9200")
            .Should().Be(ConfigurationValueRedactor.RedactedPlaceholder);

        ConfigurationValueRedactor.Redact(Property(nameof(SampleOptions.Username)), "svc-account")
            .Should().Be(ConfigurationValueRedactor.RedactedPlaceholder);
    }

    [UnitTest]
    public void Redact_ArbitraryUnmarkedString_IsRedactedByDefault()
    {
        // DisplayName matches none of the historical name-denylist tokens; deny-by-default
        // must still redact it so new string fields are safe without any annotation.
        ConfigurationValueRedactor.Redact(Property(nameof(SampleOptions.DisplayName)), "leak-me")
            .Should().Be(ConfigurationValueRedactor.RedactedPlaceholder);
    }

    [UnitTest]
    public void Redact_StructuredAndNestedValues_AreRedacted()
    {
        ConfigurationValueRedactor.Redact(
                Property(nameof(SampleOptions.AllowedHosts)),
                new List<string> { "a.internal", "b.internal" })
            .Should().Be(ConfigurationValueRedactor.RedactedPlaceholder);

        ConfigurationValueRedactor.Redact(
                Property(nameof(SampleOptions.Nested)),
                new SampleNested { Password = "nested-secret" })
            .Should().Be(ConfigurationValueRedactor.RedactedPlaceholder);
    }

    [UnitTest]
    public void Redact_ScalarValueTypes_AreExposed()
    {
        ConfigurationValueRedactor.Redact(Property(nameof(SampleOptions.Enabled)), true).Should().Be(true);
        ConfigurationValueRedactor.Redact(Property(nameof(SampleOptions.MaxConnections)), 25).Should().Be(25);
        ConfigurationValueRedactor.Redact(Property(nameof(SampleOptions.Mode)), SampleMode.On).Should().Be(SampleMode.On);

        var timeout = TimeSpan.FromSeconds(30);
        ConfigurationValueRedactor.Redact(Property(nameof(SampleOptions.Timeout)), timeout).Should().Be(timeout);
    }

    [UnitTest]
    public void Redact_NullValue_IsPreservedAsNull()
    {
        ConfigurationValueRedactor.Redact(Property(nameof(SampleOptions.ConnectionString)), null)
            .Should().BeNull();
    }

    [UnitTest]
    public void IsDisplaySafeType_StringsAndReferenceTypes_AreNotSafe()
    {
        ConfigurationValueRedactor.IsDisplaySafeType(typeof(string)).Should().BeFalse();
        ConfigurationValueRedactor.IsDisplaySafeType(typeof(Uri)).Should().BeFalse();
        ConfigurationValueRedactor.IsDisplaySafeType(typeof(Dictionary<string, string>)).Should().BeFalse();
    }

    [UnitTest]
    public void IsDisplaySafeType_ScalarAndNullableValueTypes_AreSafe()
    {
        ConfigurationValueRedactor.IsDisplaySafeType(typeof(bool)).Should().BeTrue();
        ConfigurationValueRedactor.IsDisplaySafeType(typeof(int?)).Should().BeTrue();
        ConfigurationValueRedactor.IsDisplaySafeType(typeof(SampleMode)).Should().BeTrue();
        ConfigurationValueRedactor.IsDisplaySafeType(typeof(TimeSpan)).Should().BeTrue();
        ConfigurationValueRedactor.IsDisplaySafeType(typeof(Guid)).Should().BeTrue();
    }
}
