// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Verifies the admin configuration-discovery value redactor is deny-by-default: it masks credentials
/// embedded in generically named fields (ConnectionString/DefaultConnection/Endpoint/Username) and any
/// value that looks credential-bearing regardless of property name (honua-server#2804), while leaving
/// innocuous values readable.
/// </summary>
public sealed class ConfigurationSecretRedactorTests
{
    [UnitTest]
    public void Redact_ConnectionStringPropertyWithEmbeddedPassword_IsMasked()
    {
        // Property name is NOT on the legacy password/secret/key/token denylist, yet the value leaks a
        // password — this is the exact leak reported in honua-server#2804.
        const string connectionString =
            "Host=db.internal;Port=5432;Database=honua;Username=honua;Password=sup3r-s3cret!";

        var redacted = ConfigurationSecretRedactor.Redact("ConnectionString", connectionString);

        redacted.Should().Be(ConfigurationSecretRedactor.Mask);
        redacted.Should().NotBe(connectionString);
    }

    [UnitTest]
    public void Redact_DefaultConnectionName_IsMaskedByName()
    {
        var redacted = ConfigurationSecretRedactor.Redact("DefaultConnection", "Host=db;Database=honua");

        redacted.Should().Be(ConfigurationSecretRedactor.Mask);
    }

    [UnitTest]
    public void Redact_EndpointWithEmbeddedCredentials_IsMasked()
    {
        var redacted = ConfigurationSecretRedactor.Redact(
            "Endpoint",
            "https://admin:hunter2@service.internal:9200/");

        redacted.Should().Be(ConfigurationSecretRedactor.Mask);
    }

    [UnitTest]
    public void Redact_InnocuousValueOnSafeName_IsReturnedRaw()
    {
        var redacted = ConfigurationSecretRedactor.Redact("MaxPageSize", "1000");

        redacted.Should().Be("1000");
    }

    [UnitTest]
    public void Redact_CredentialBearingValueOnInnocuousName_IsMasked()
    {
        // Deny-by-default: a brand-new field that happens to carry a connection string is masked even
        // though its name is not on any list.
        var redacted = ConfigurationSecretRedactor.Redact(
            "PrimaryStore",
            "AccountName=honua;AccountKey=abcd1234efgh5678==;EndpointSuffix=core.windows.net");

        redacted.Should().Be(ConfigurationSecretRedactor.Mask);
    }

    [UnitTest]
    public void Redact_NullValue_StaysNull()
    {
        ConfigurationSecretRedactor.Redact("Password", null).Should().BeNull();
        ConfigurationSecretRedactor.Redact("Endpoint", null).Should().BeNull();
    }

    [UnitTest]
    public void LooksCredentialBearing_HighEntropyToken_IsDetected()
    {
        ConfigurationSecretRedactor
            .LooksCredentialBearing("dGhpc2lzYXZlcnlsb25nYW5kaGlnaGVudHJvcHlzZWNyZXR0b2tlbjEyMzQ1Ng")
            .Should().BeTrue();
    }

    [UnitTest]
    public void LooksCredentialBearing_PlainSentence_IsNotDetected()
    {
        ConfigurationSecretRedactor
            .LooksCredentialBearing("This is a perfectly ordinary configuration description value.")
            .Should().BeFalse();
    }
}
