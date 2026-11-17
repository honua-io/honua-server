// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

[Trait("Tier", "Fast")]
public sealed class MigrationEvidenceReferencesTests
{
    [Theory]
    [InlineData("https://user:password@example.test/run.json?token=value#fragment", "https://example.test/run.json")]
    [InlineData("https://example.test/run.json#fragment?token=value", "https://example.test/run.json")]
    [InlineData("artifacts/run.json?token=value", "artifacts/run.json")]
    [InlineData("artifacts/run.json#fragment", "artifacts/run.json")]
    [InlineData("s3://evidence/run.json?signature=value", "s3://evidence/run.json")]
    [InlineData("https://EXAMPLE.test:443/run%20one.json", "https://EXAMPLE.test:443/run%20one.json")]
    [InlineData("http://[invalid?token=value", "http://[invalid")]
    public void Sanitize_RedactsSensitiveTailsWithoutRewritingReferencePaths(string input, string expected)
    {
        MigrationEvidenceReferences.Sanitize([input]).Should().Equal(expected);
    }

    [Fact]
    public void Sanitize_RemovesEmptyReferencesAndDeduplicatesAfterRedaction()
    {
        var result = MigrationEvidenceReferences.Sanitize(
        [
            " ", "", "?token=value", "#fragment",
            " artifacts/z.json?first=value ", "artifacts/z.json#second",
            "https://user:password@example.test/a.json?token=value",
            "https://example.test/a.json", "Artifacts/z.json"
        ]);

        result.Should().Equal(
            "Artifacts/z.json", "artifacts/z.json", "https://example.test/a.json");
    }
}
