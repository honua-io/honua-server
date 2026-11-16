// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.RealAwsCertification)]
public sealed class AwsLambdaAliasRealCertificationTests : IClassFixture<RealAwsCertificationFixture>
{
    private readonly RealAwsCertificationFixture _cert;
    private readonly ITestOutputHelper _output;

    public AwsLambdaAliasRealCertificationTests(RealAwsCertificationFixture cert, ITestOutputHelper output)
    {
        _cert = cert;
        _output = output;
    }

    [SkippableFact]
    public async Task ReadFlipRestore_CertifiesAliasFlipPrimitive_ThroughProductionSeam()
    {
        Skip.IfNot(
            _cert.LambdaAliasConfigured,
            "Real-AWS Lambda alias cell not configured (needs HONUA_REALAWS_CERT_ENABLED=true, "
            + "HONUA_REALAWS_CERT_LAMBDA_FUNCTION and HONUA_REALAWS_CERT_LAMBDA_ALIAS with credentials present).");

        var client = new AwsSdkLambdaAliasClient();
        var function = _cert.LambdaFunctionName!;
        var alias = _cert.LambdaAliasName!;
        var region = _cert.Region;

        // Capture the original alias state so the restore is exact.
        var original = await client.GetAliasAsync(function, alias, region);
        original.FunctionVersion.Should().NotBeNullOrWhiteSpace(
            "a live alias must resolve to a concrete function version");

        var originalWeights = original.AdditionalVersionWeights.Count > 0
            ? new Dictionary<string, double>(original.AdditionalVersionWeights, StringComparer.Ordinal)
            : null;

        var restoreRequired = true;
        try
        {
            // Flip the alias to the SAME version AND its EXISTING weights (no traffic change) — this is
            // the production UpdateAliasAsync control-plane call the backend's cutover/rollback are built
            // on. We MUST pass originalWeights (not null): a null/empty weight map makes the seam CLEAR
            // any live AdditionalVersionWeights, so a null flip on a weighted alias would drop the canary
            // routing, and a kill before the restore would lose it permanently.
            var flipped = await client.UpdateAliasAsync(
                function,
                alias,
                original.FunctionVersion!,
                additionalVersionWeights: originalWeights,
                region);

            flipped.FunctionVersion.Should().Be(
                original.FunctionVersion,
                "the alias-flip primitive must land the alias on the requested version");
            flipped.AdditionalVersionWeights.Should().BeEquivalentTo(
                original.AdditionalVersionWeights,
                "the weight-preserving flip must not alter the alias's traffic split");

            // Restore explicitly (idempotent with the finally), then mark the finally a no-op.
            await client.UpdateAliasAsync(function, alias, original.FunctionVersion!, originalWeights, region);

            var restored = await client.GetAliasAsync(function, alias, region);
            restored.FunctionVersion.Should().Be(
                original.FunctionVersion,
                "the alias must be restored to its original version");
            restored.AdditionalVersionWeights.Should().BeEquivalentTo(
                original.AdditionalVersionWeights,
                "the alias must be restored to its original traffic split (weights), not just its version");
            restoreRequired = false;
        }
        finally
        {
            if (restoreRequired)
            {
                try
                {
                    // Guaranteed restore if an assertion above threw after the flip: never leave the
                    // dedicated cert function's alias mutated — restore both the version AND the weights.
                    await client.UpdateAliasAsync(function, alias, original.FunctionVersion!, originalWeights, region);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Best-effort: the flip preserved the original version AND weights, so even a failed
                    // restore leaves the alias serving the original traffic split unchanged. Log it so a
                    // failed teardown is visible in CI rather than silently swallowed.
                    _output.WriteLine(
                        $"[cert] best-effort alias restore for '{function}:{alias}' failed: {ex.Message}");
                }
            }
        }
    }
}
