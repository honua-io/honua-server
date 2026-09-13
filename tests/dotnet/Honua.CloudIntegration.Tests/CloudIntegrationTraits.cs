// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Shared constants for the cloud-integration trait taxonomy (#2163). Every test in this
/// project is tagged <c>[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CloudIntegration)]</c>
/// so the default PR test run — which filters <c>Category!=CloudIntegration</c> on the
/// Honua.Server.Tests projects and never invokes <c>dotnet test</c> against this project —
/// excludes it. The dedicated cloud-integration workflow opts in with
/// <c>--filter "Category=CloudIntegration"</c>.
/// </summary>
internal static class CloudIntegrationTraits
{
    /// <summary>
    /// xUnit trait key. Mirrors the <c>Category</c> key used by the rest of the suite so a
    /// single <c>Category=CloudIntegration</c> / <c>Category!=CloudIntegration</c> filter
    /// governs inclusion and exclusion uniformly.
    /// </summary>
    public const string Category = "Category";

    /// <summary>
    /// Trait value applied to every Docker-backed cloud-integration test.
    /// </summary>
    public const string CloudIntegration = "CloudIntegration";

    /// <summary>
    /// Trait value applied to the ADR-0060 substrate-neutral local lane (#2166, #2457). These
    /// tests prove the single-host, zero-cloud (containers only, no Kubernetes, no cloud) deploy /
    /// rolling-upgrade / expand-contract-migration / rollback story on BOTH planes (serving via
    /// <c>YarpRollingDeployBackend</c> + real embedded YARP; GP via
    /// <c>LocalProcessPoolBatchComputeBackend</c>) plus the real
    /// <c>PostgresDatabaseMigrationRunner</c> expand/contract gate. They are kept in a distinct
    /// category so the dedicated <c>local-substrate</c> workflow job opts in with
    /// <c>--filter "Category=LocalSubstrate"</c>, and every Docker-dependent test
    /// <c>[SkippableFact]</c>-skips (never fails) when Docker is unavailable.
    /// </summary>
    public const string LocalSubstrate = "LocalSubstrate";

    /// <summary>
    /// Trait value applied to the real-AWS certification lane (#2164). These tests target a
    /// LIVE AWS account (not an emulator) and are kept in a distinct category so neither the
    /// default PR run nor the emulated <c>cloud-integration-harness.yml</c>
    /// (<c>--filter "Category=CloudIntegration"</c>) ever invokes them. They run only in the
    /// dedicated, OIDC-gated <c>real-aws-certification.yml</c> workflow (or locally with
    /// <c>HONUA_REALAWS_CERT_ENABLED=true</c> and credentials present); every test
    /// <c>[SkippableFact]</c>-skips — never fails — when the lane is not enabled, so
    /// the project still builds and runs (skipping) on forks and credential-less environments.
    /// </summary>
    public const string RealAwsCertification = "RealAwsCertification";

    /// <summary>
    /// Trait value applied to the exact-candidate deploy-gate lane (honua-server#4617). These tests
    /// roll a real published Honua server image (the candidate) over a real previous image through
    /// the self-hosted rolling backend, with a real Prometheus scraping the candidate, and inject
    /// failures. They need both image references (<c>HONUA_CANDIDATE_IMAGE</c> and
    /// <c>HONUA_PREVIOUS_IMAGE</c>), so they are kept out of the <c>CloudIntegration</c> and
    /// <c>LocalSubstrate</c> filters and run only when a release or operator names the candidate;
    /// every test <c>[SkippableFact]</c>-skips when the images or Docker are not available.
    /// </summary>
    public const string CandidateCertification = "CandidateCertification";
}
