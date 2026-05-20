// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Slice 4 of issue #1015: deterministic evidence-pack generation.
/// </summary>
public sealed class MigrationEvidencePackBuilderTests
{
    [Fact]
    public void Build_ProducesDeterministicFingerprint_ForIdenticalInputs()
    {
        var inputs = BuildInputs();

        var first = MigrationEvidencePackBuilder.Build(inputs, new MigrationEvidencePackBuilderOptions
        {
            RunId = "nightly-20260519",
            Generator = "test/1.0",
            GeneratedAt = DateTimeOffset.Parse("2026-05-19T00:00:00Z", CultureInfo.InvariantCulture)
        });

        var second = MigrationEvidencePackBuilder.Build(inputs, new MigrationEvidencePackBuilderOptions
        {
            // Different run-time metadata; fingerprint must be unaffected.
            RunId = "nightly-20260601",
            Generator = "test/2.0",
            GeneratedAt = DateTimeOffset.Parse("2026-06-01T12:34:56Z", CultureInfo.InvariantCulture)
        });

        first.BundleFingerprint.Should().StartWith("sha256:");
        first.BundleFingerprint.Should().Be(second.BundleFingerprint,
            "fingerprint must cover the bundle only — wall-clock and generator labels are excluded so nightly re-runs stay byte-identical.");

        first.RunId.Should().Be("nightly-20260519");
        second.RunId.Should().Be("nightly-20260601");
    }

    [Fact]
    public void Build_FingerprintChanges_WhenAStepResultChanges()
    {
        var inputs = BuildInputs();
        var mutated = inputs with
        {
            ApplyExecution = inputs.ApplyExecution with
            {
                StepResults = inputs.ApplyExecution.StepResults
                    .Select(step => step.StepId == "step:catalog:roads"
                        ? step with { Outcome = "manual-review", Message = "Operator review required." }
                        : step)
                    .ToArray()
            }
        };

        var baseline = MigrationEvidencePackBuilder.Build(inputs);
        var changed = MigrationEvidencePackBuilder.Build(mutated);

        baseline.BundleFingerprint.Should().NotBe(changed.BundleFingerprint,
            "any change to the bundle inputs must propagate to the fingerprint.");
    }

    [Fact]
    public void Build_GroupsStepResultsIntoCanonicalStages()
    {
        var inputs = BuildInputs();

        var pack = MigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.Stages.Should().HaveCount(3);
        pack.Bundle.Stages.Select(s => s.Id).Should().Equal(
            MigrationEvidencePackStageIds.Catalog,
            MigrationEvidencePackStageIds.Data,
            MigrationEvidencePackStageIds.Style);

        var catalog = pack.Bundle.Stages.Single(s => s.Id == MigrationEvidencePackStageIds.Catalog);
        catalog.StepCount.Should().Be(2);
        catalog.AppliedCount.Should().Be(2);

        var data = pack.Bundle.Stages.Single(s => s.Id == MigrationEvidencePackStageIds.Data);
        data.StepCount.Should().Be(1);
        data.AlreadyAppliedCount.Should().Be(1);

        var style = pack.Bundle.Stages.Single(s => s.Id == MigrationEvidencePackStageIds.Style);
        style.StepCount.Should().Be(2);
        style.AppliedCount.Should().Be(1);
        style.ManualReviewCount.Should().Be(1);
    }

    [Fact]
    public void Build_AggregatesStyleDiagnostics_FromManualReviewSteps()
    {
        var inputs = BuildInputs();

        var pack = MigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.StyleDiagnostics.Should().HaveCount(1);
        var diagnostic = pack.Bundle.StyleDiagnostics[0];
        diagnostic.SourceId.Should().Be("style:ops:line");
        diagnostic.StepOutcome.Should().Be("manual-review");
        diagnostic.Message.Should().Contain("manual-review");

        pack.Bundle.Summary.StyleManualReviewCount.Should().Be(1,
            "slice-3 manual-review style outcomes must be surfaced so the pack does not claim visual parity for them.");
    }

    [Fact]
    public void Build_RedactsCredentials_FromSourceUrls()
    {
        var inputs = BuildInputs();
        var withSecretUrl = inputs with
        {
            ApplyExecution = inputs.ApplyExecution with
            {
                Source = inputs.ApplyExecution.Source with
                {
                    BaseUrl = "https://admin:hunter2@geoserver.example.com:8443/geoserver/rest?token=topsecret"
                }
            },
            Inventory = inputs.Inventory with
            {
                Source = inputs.Inventory.Source with
                {
                    BaseUrl = "https://admin:hunter2@geoserver.example.com:8443/geoserver/rest?token=topsecret"
                }
            },
            Manifest = inputs.Manifest with
            {
                Source = inputs.Manifest.Source with
                {
                    BaseUrl = "https://admin:hunter2@geoserver.example.com:8443/geoserver/rest?token=topsecret"
                }
            }
        };

        var pack = MigrationEvidencePackBuilder.Build(withSecretUrl);

        pack.Bundle.Source.BaseUrl.Should().NotContain("hunter2");
        pack.Bundle.Source.BaseUrl.Should().NotContain("admin");
        pack.Bundle.Source.BaseUrl.Should().NotContain("topsecret");
        pack.Bundle.Source.BaseUrl.Should().Be("https://geoserver.example.com:8443/geoserver/rest");

        pack.Bundle.Inventory.Source.BaseUrl.Should().NotContain("hunter2");
        pack.Bundle.Inventory.Source.BaseUrl.Should().NotContain("topsecret");
        pack.Bundle.Manifest.Source.BaseUrl.Should().NotContain("hunter2");
        pack.Bundle.Manifest.Source.BaseUrl.Should().NotContain("topsecret");

        // Serialize the entire pack and assert no credential leaked anywhere.
        var json = JsonSerializer.Serialize(pack, MigrationEvidencePackJsonContext.Default.MigrationEvidencePackArtifact);
        json.Should().NotContain("hunter2");
        json.Should().NotContain("topsecret");
        // "admin" is a common substring; assert via user-info marker instead.
        json.Should().NotContain("admin:hunter2");
    }

    [Fact]
    public void Build_CapturesWorkspaceScope_WhenOperatorRestrictsRun()
    {
        var inputs = BuildInputs() with { RequestedWorkspaceNames = new[] { "ops", "ops" } };

        var pack = MigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.WorkspaceScope.Restricted.Should().BeTrue();
        pack.Bundle.WorkspaceScope.WorkspaceNames.Should().Equal("ops");
    }

    [Fact]
    public void Build_CapturesWorkspaceScope_AsUnrestricted_WhenNoNamesProvided()
    {
        var inputs = BuildInputs();

        var pack = MigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.WorkspaceScope.Restricted.Should().BeFalse();
        pack.Bundle.WorkspaceScope.WorkspaceNames.Should().BeEmpty();
    }

    /// <summary>
    /// Nightly fixture emission hook (slice 4 of #1015). When the
    /// <c>HONUA_EMIT_EVIDENCE_PACK</c> environment variable is set the
    /// fixture-driven evidence-pack JSON is written to the path supplied via
    /// <c>HONUA_EVIDENCE_PACK_OUTPUT</c> so the nightly workflow can upload it
    /// as a CI artifact. The contents are deterministic — the same fixture
    /// always produces the same fingerprint — so workflow consumers can diff
    /// or pin the artifact across runs.
    /// </summary>
    [Fact]
    public void EmitNightlyEvidencePack_WhenEnvVarSet_WritesDeterministicArtifact()
    {
        if (Environment.GetEnvironmentVariable("HONUA_EMIT_EVIDENCE_PACK") != "1")
        {
            return;
        }

        var outputPath = Environment.GetEnvironmentVariable("HONUA_EVIDENCE_PACK_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(AppContext.BaseDirectory, "geoserver-migration-evidence-pack.json");
        }

        var pack = MigrationEvidencePackBuilder.Build(
            BuildInputs(),
            new MigrationEvidencePackBuilderOptions
            {
                RunId = Environment.GetEnvironmentVariable("HONUA_EVIDENCE_PACK_RUN_ID") ?? "nightly-fixture",
                Generator = "honua.migration.evidence-pack-builder/1.0",
                GeneratedAt = DateTimeOffset.UnixEpoch
            });

        // Serialize via the source-gen context (AOT-safe) and re-format with an
        // indented JsonDocument round-trip so the artifact stays human-
        // readable for reviewers without losing trim safety.
        var compactJson = JsonSerializer.Serialize(
            pack,
            MigrationEvidencePackJsonContext.Default.MigrationEvidencePackArtifact);
        using var document = JsonDocument.Parse(compactJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            document.RootElement.WriteTo(writer);
        }

        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath, json);
        pack.BundleFingerprint.Should().StartWith("sha256:");
    }

    [Fact]
    public void Artifact_Shape_HasStableTopLevelFields()
    {
        // Schema-stability guard: surface any accidental addition/rename of
        // the evidence-pack contract so reviewers update consumers
        // (admin UI, SDK orchestration in slice 5).
        var pack = MigrationEvidencePackBuilder.Build(BuildInputs());
        var json = JsonSerializer.Serialize(
            pack,
            MigrationEvidencePackJsonContext.Default.MigrationEvidencePackArtifact);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Top-level artifact contract.
        root.GetProperty("artifactKind").GetString().Should().Be("honua.migration.evidence-pack");
        root.GetProperty("artifactVersion").GetString().Should().Be("1.0");
        root.GetProperty("runId").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("generator").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("generatedAt").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("bundleFingerprint").GetString().Should().StartWith("sha256:");

        // Bundle contract: catalog/data/style stages plus inventory/manifest
        // snapshots so downstream consumers can audit the entire run from a
        // single artifact.
        var bundle = root.GetProperty("bundle");
        bundle.GetProperty("sourceKind").ValueKind.Should().Be(JsonValueKind.String);
        bundle.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("workspaceScope").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("apply").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("summary").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("stages").ValueKind.Should().Be(JsonValueKind.Array);
        bundle.GetProperty("styleDiagnostics").ValueKind.Should().Be(JsonValueKind.Array);
        bundle.GetProperty("inventory").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("manifest").ValueKind.Should().Be(JsonValueKind.Object);

        var apply = bundle.GetProperty("apply");
        apply.GetProperty("planFingerprint").ValueKind.Should().Be(JsonValueKind.String);
        apply.GetProperty("replayToken").ValueKind.Should().Be(JsonValueKind.String);
        apply.GetProperty("executionMode").ValueKind.Should().Be(JsonValueKind.String);

        var summary = bundle.GetProperty("summary");
        summary.GetProperty("totalStepCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("appliedStepCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("alreadyAppliedStepCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("manualReviewStepCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("unsupportedStepCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("failedStepCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("styleManualReviewCount").ValueKind.Should().Be(JsonValueKind.Number);
    }

    private static MigrationEvidencePackInputs BuildInputs()
    {
        var source = new MigrationSourceIdentity
        {
            DisplayName = "GeoServer Sample",
            BaseUrl = "https://geoserver.example.com/geoserver/rest",
            Product = "GeoServer",
            Version = "2.24.0",
            ServiceType = "REST"
        };

        var inventory = new MigrationSourceInventoryArtifact
        {
            SourceKind = "geoserver-rest",
            Source = source,
            AuthPosture = new MigrationInventoryAuthPosture { Mode = "basic", CredentialsSupplied = true, AccessConfirmed = true },
            ScanCompleteness = new MigrationInventoryCompleteness { Status = "complete" },
            Summary = new MigrationInventorySummary { ContainerCount = 1, ResourceCount = 2, StyleCount = 2 },
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "Source inventory is compatible with Honua migration."
            }
        };

        var manifest = new MigrationManifestArtifact
        {
            SourceKind = "geoserver-rest",
            Source = source,
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = 2,
                TargetResourceCount = 2,
                StyleActionCount = 2
            }
        };

        var stepResults = new[]
        {
            new MigrationApplyExecutionStepResult
            {
                StepId = "step:catalog:roads",
                SourceId = "workspace:ops",
                Kind = "workspace",
                Action = "stage-catalog-service",
                Disposition = "ready",
                Outcome = "applied",
                Message = "Workspace 'ops' was created in honua.migration_services."
            },
            new MigrationApplyExecutionStepResult
            {
                StepId = "step:catalog:parcels",
                SourceId = "layer-group:ops:parcels",
                Kind = "layer-group",
                Action = "stage-catalog-service",
                Disposition = "ready",
                Outcome = "applied",
                Message = "Layer-group catalog row created."
            },
            new MigrationApplyExecutionStepResult
            {
                StepId = "step:data:ops-pg",
                SourceId = "datastore:ops:ops-pg",
                Kind = "datastore",
                Action = "stage-data-source",
                Disposition = "ready",
                Outcome = "already-applied",
                Message = "Data source already present; re-apply was a no-op."
            },
            new MigrationApplyExecutionStepResult
            {
                StepId = "step:style:point",
                SourceId = "style:ops:point",
                Kind = "style",
                Action = "stage-style",
                Disposition = "ready",
                Outcome = "applied",
                Message = "Applied sld style 'style:ops:point' to honua.migration_styles."
            },
            new MigrationApplyExecutionStepResult
            {
                StepId = "step:style:line",
                SourceId = "style:ops:line",
                Kind = "style",
                Action = "stage-style",
                Disposition = "ready",
                Outcome = "manual-review",
                Message = "Persisted style 'style:ops:line' with manual-review disposition. Recorded 1 error and 0 warning conversion diagnostic(s). Do not claim visual parity until the diagnostics are resolved."
            }
        };

        var applyExecution = new MigrationApplyExecutionArtifact
        {
            SourceKind = "geoserver-rest",
            Source = source,
            PlanFingerprint = "sha256:plan-fingerprint",
            ReplayToken = "sha256:replay-token",
            StartedAt = DateTimeOffset.Parse("2026-05-19T00:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-05-19T00:01:00Z", CultureInfo.InvariantCulture),
            Summary = new MigrationApplyExecutionSummary
            {
                TotalStepCount = stepResults.Length,
                AppliedStepCount = stepResults.Count(r => r.Outcome == "applied"),
                AlreadyAppliedStepCount = stepResults.Count(r => r.Outcome == "already-applied"),
                ManualReviewStepCount = stepResults.Count(r => r.Outcome == "manual-review"),
                UnsupportedStepCount = 0,
                FailedStepCount = 0
            },
            StepResults = stepResults
        };

        return new MigrationEvidencePackInputs
        {
            Inventory = inventory,
            Manifest = manifest,
            ApplyExecution = applyExecution
        };
    }
}
