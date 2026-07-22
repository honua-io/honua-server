// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Real-AWS certification lane (#2164) — the Lambda alias-flip cell. Certifies the alias
/// read → flip → restore SDK path that <see cref="AwsLambdaGitOpsDeployBackend"/>'s cutover and
/// rollback rides, at the NARROWEST real-SDK level, through the PRODUCTION seam
/// (<see cref="AwsSdkLambdaAliasClient"/> — the same <c>UpdateAlias</c>/<c>GetAlias</c> client the
/// backend is wired with in production).
///
/// CONSERVATIVE-BY-DESIGN (documented rationale): a faithful full-backend deploy certification would
/// need a published deploy artifact + telemetry connection + a distinct target function version to
/// flip between, which mutates a production-relevant function and needs standing rollout
/// infrastructure. Instead this cell targets ONLY the dedicated certification-stack function and
/// performs a NO-OP flip: read the alias's current <c>FunctionVersion</c> and routing weights, then
/// <c>UpdateAlias</c> the alias to that SAME version AND its EXISTING weights (exercising the exact
/// production <c>UpdateAliasAsync</c> control-plane call — the alias-flip primitive both cutover and
/// rollback are built on), and RESTORE the original version + weights in a guaranteed <c>finally</c>.
/// Preserving the existing weights on the flip is load-bearing: the production seam sends an
/// <c>AliasRoutingConfiguration</c> whose empty weight map CLEARS any <c>AdditionalVersionWeights</c>,
/// so flipping with a null/empty weight map would drop a live weighted (canary) routing config — a
/// kill between that flip and the restore would then lose the routing permanently. It never changes
/// which version serves traffic nor the traffic split, so it certifies the alias-flip control-plane
/// mechanics without a deploy artifact and without risk to a production-relevant function. The
/// weighted-cutover and rollback-decision mechanics themselves stay covered by the ECS/ALB live
/// certification cells, including the rollback-failure path in
/// <see cref="AwsEcsAlbRollbackFailureRealCertificationTests"/>.
///
/// Alias updates are in-place mutations of a standing resource, not created resources, so there is
/// nothing to tag or reap; correctness is the guaranteed restore. OFF unless
/// <see cref="RealAwsCertificationFixture.LambdaAliasConfigured"/>; skips otherwise.
/// </summary>
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
