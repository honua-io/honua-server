// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Logging;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Logging;

public sealed class ProviderErrorBodyRedactorTests
{
    [UnitTheory]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig", "eyJhbGciOi")]
    [InlineData("{\"access_token\":\"tok-123\"}", "tok-123")]
    [InlineData("https://acct.blob.core.windows.net/c?sv=2024&sig=abcDEF%2B", "abcDEF")]
    [InlineData("password=hunter2;user=me", "hunter2")]
    [InlineData("see https://user:pa55@host.example/path", "pa55")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=a;AccountKey=c2VjcmV0a2V5==;", "c2VjcmV0a2V5")]
    [InlineData("token rejected: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln", "eyJzdWIiOiIxIn0")]
    public void Redact_RemovesCredentialValues(string body, string secret)
    {
        var redacted = ProviderErrorBodyRedactor.Redact(body);

        redacted.Should().NotContain(secret).And.Contain(CredentialPathRedaction.Redacted);
    }

    [UnitTest]
    public void Redact_TruncatesLongBodiesAndMarksTheCut()
    {
        var redacted = ProviderErrorBodyRedactor.Redact(new string('a', 5000));

        redacted.Length.Should().Be(ProviderErrorBodyRedactor.DefaultMaxLength + 1);
        redacted.Should().EndWith("…");
    }

    [UnitTest]
    public void Redact_StripsControlCharactersSoBodiesCannotForgeLogLines()
    {
        ProviderErrorBodyRedactor.Redact("bad request\r\nINFO forged").Should().NotContain("\n").And.NotContain("\r");
    }

    [UnitTest]
    public void Redact_KeepsOrdinaryErrorText()
    {
        ProviderErrorBodyRedactor.Redact("{\"code\":\"PoolNotFound\",\"message\":\"The specified pool does not exist.\"}")
            .Should().Contain("PoolNotFound").And.Contain("does not exist");
    }

    [UnitTest]
    public void Redact_KeepsExceptionNamesThatContainCredentialWords()
        => ProviderErrorBodyRedactor.Redact("ExpiredTokenException: The security token included in the request is expired")
            .Should().Contain("The security token");

    [UnitTest]
    public void Redact_HugeBody_KeepsTheLeadingDiagnostic()
    {
        var body = "<html>Service Unavailable" + string.Concat(Enumerable.Repeat("<div class=\"x\">padding</div>", 200_000));

        var redacted = ProviderErrorBodyRedactor.Redact(body);

        redacted.Should().StartWith("<html>Service Unavailable").And.EndWith("…");
    }
}
