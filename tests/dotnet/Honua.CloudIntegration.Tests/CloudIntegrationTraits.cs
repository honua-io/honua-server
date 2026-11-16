// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    // Single-host lane with no Kubernetes and no cloud: the local process-pool batch backend and the
    // real migration runner's expand/contract gate. The local-substrate workflow job selects it with
    // --filter "Category=LocalSubstrate"; Docker-dependent tests skip when Docker is unavailable.
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

    // Exact-candidate lanes run a published server image named by HONUA_CANDIDATE_IMAGE, so they stay
    // out of the CloudIntegration and LocalSubstrate filters. Tests skip when the image or Docker is missing.
    public const string CandidateCertification = "CandidateCertification";
}
