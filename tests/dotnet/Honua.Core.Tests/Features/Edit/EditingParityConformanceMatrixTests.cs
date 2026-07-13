// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Tests.Features.Edit;

/// <summary>
/// Editing-parity conformance matrix (#2137): the closing acceptance artifact for epic #1270.
/// Each supported row is executed against the shared edit-path engines so a regression of a
/// previously-passing capability fails CI. The human-readable matrix (expected Esri behavior,
/// Honua behavior, supported/partial/unsupported, and the non-Postgres / safe-Arcade caveats)
/// is the docs receipt at <c>docs/internal/evidence/editing-parity-conformance.md</c>.
/// </summary>
public sealed class EditingParityConformanceMatrixTests
{
    /// <summary>Supported / partial / unsupported parity status of a matrix row.</summary>
    public enum ParityStatus
    {
        /// <summary>Capability is fully implemented and executed by the matrix.</summary>
        Supported,

        /// <summary>Capability is partially implemented; tracked for completion in the receipt.</summary>
        Partial,

        /// <summary>Capability is not implemented.</summary>
        Unsupported
    }

    /// <summary>A single conformance row tying a capability to its asserted edit-path behavior.</summary>
    /// <param name="Capability">Stable capability key.</param>
    /// <param name="Status">The capability's parity status.</param>
    /// <param name="Verify">Executable assertion for supported rows; a no-op for non-supported rows.</param>
    public sealed record MatrixRow(string Capability, ParityStatus Status, Action Verify);

    /// <summary>Supplies the supported capability keys for the executable matrix theory.</summary>
    /// <returns>The supported capability keys.</returns>
    public static TheoryData<string> SupportedCapabilities()
    {
        var data = new TheoryData<string>();
        foreach (var row in BuildMatrix().Where(r => r.Status == ParityStatus.Supported))
        {
            data.Add(row.Capability);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SupportedCapabilities))]
    public void Matrix_SupportedCapability_BehavesAsConformanceExpects(string capability)
    {
        var row = BuildMatrix().Single(r => r.Capability == capability);

        // Executing the row asserts the capability still behaves; a regression throws and fails.
        row.Verify();
    }

    [Fact]
    public void Matrix_CoversEveryHardenedChildCapability()
    {
        var matrix = BuildMatrix();

        // Guards against silently dropping a row: each editing-parity child must contribute at
        // least one matrix capability so the suite fails if coverage regresses.
        matrix.Should().Contain(r => r.Capability.StartsWith("owner.", StringComparison.Ordinal));
        matrix.Should().Contain(r => r.Capability.StartsWith("contingent.", StringComparison.Ordinal));
        matrix.Should().Contain(r => r.Capability.StartsWith("attributeRule.", StringComparison.Ordinal));
        matrix.Should().Contain(r => r.Capability.StartsWith("versionManagement.", StringComparison.Ordinal));
    }

    private static IReadOnlyList<MatrixRow> BuildMatrix()
    {
        var ownerPolicy = new MetadataV2OwnerEditPolicy { Enabled = true, OwnerField = "owner" };
        var contingentResource = ContingentResource();

        return
        [
            // ---- Owner-based edit policy (#2132) ----
            new("owner.match.allow", ParityStatus.Supported, () =>
                OwnerEditPolicyEvaluator.Evaluate(
                    ownerPolicy, AttributeRuleEditEvent.Update, "alice",
                    new EditPrincipal("alice", true, false)).IsAllowed.Should().BeTrue()),
            new("owner.mismatch.deny", ParityStatus.Supported, () =>
                OwnerEditPolicyEvaluator.Evaluate(
                    ownerPolicy, AttributeRuleEditEvent.Update, "bob",
                    new EditPrincipal("alice", true, false)).IsAllowed.Should().BeFalse()),
            new("owner.admin.override", ParityStatus.Supported, () =>
                OwnerEditPolicyEvaluator.Evaluate(
                    ownerPolicy, AttributeRuleEditEvent.Delete, "bob",
                    new EditPrincipal("root", true, true)).IsAllowed.Should().BeTrue()),
            new("owner.anonymous.deny", ParityStatus.Supported, () =>
                OwnerEditPolicyEvaluator.Evaluate(
                    ownerPolicy, AttributeRuleEditEvent.Insert, null,
                    EditPrincipal.Anonymous).IsAllowed.Should().BeFalse()),

            // ---- Contingent values (#2133) ----
            new("contingent.valid.accept", ParityStatus.Supported, () =>
                ContingentValueValidator.Validate(
                    contingentResource, Attributes(("color", "red"), ("size", "S")))
                    .IsValid.Should().BeTrue()),
            new("contingent.invalid.reject", ParityStatus.Supported, () =>
                ContingentValueValidator.Validate(
                    contingentResource, Attributes(("color", "red"), ("size", "L")))
                    .IsValid.Should().BeFalse()),
            new("contingent.partialUpdate.merge", ParityStatus.Supported, () =>
                ContingentValueValidator.Validate(
                    contingentResource, Attributes(("size", "S")))
                    .IsValid.Should().BeFalse()),

            // ---- Attribute-rule depth (#2134) ----
            new("attributeRule.immediateVsBatch", ParityStatus.Supported, () =>
                AttributeRuleEngine.Apply(
                    BatchRuleResource(), Attributes(("qty", 4)), AttributeRuleEditEvent.Insert)
                    .Attributes.ContainsKey("total").Should().BeFalse()),
            new("attributeRule.triggeringEvents", ParityStatus.Supported, () =>
                AttributeRuleEngine.Apply(
                    InsertOnlyConstraintResource(), Attributes(("qty", -1)), AttributeRuleEditEvent.Update)
                    .IsValid.Should().BeTrue()),
            new("attributeRule.exclusion", ParityStatus.Supported, () =>
                AttributeRuleEngine.Apply(
                    ExclusionResource(), Attributes(("status", "draft")), AttributeRuleEditEvent.Update)
                    .IsValid.Should().BeFalse()),
            new("attributeRule.safeArcade.unsupportedRoutedOutOfScope", ParityStatus.Supported, () =>
                AttributeRuleEngine.Apply(
                    UnsupportedArcadeResource(), Attributes(("qty", 1)), AttributeRuleEditEvent.Insert)
                    .IsValid.Should().BeTrue()),

            // ---- VersionManagementServer reconcile shapes (#2135) — partial, tracked in receipt ----
            new("versionManagement.reconcile.byObject", ParityStatus.Partial, static () => { }),
            new("versionManagement.reconcile.withPost", ParityStatus.Partial, static () => { }),
            new("versionManagement.resolveConflicts.shape", ParityStatus.Partial, static () => { }),
        ];
    }

    private static MetadataV2Resource ContingentResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res", Name = "Contingent" },
        ContingentValueGroups =
        [
            new MetadataV2ContingentValueGroup
            {
                Name = "colorSize",
                Restrictive = true,
                Fields = ["color", "size"],
                ContingentValues =
                [
                    new MetadataV2ContingentValue
                    {
                        Id = 1,
                        Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["color"] = new() { Type = "code", Code = JsonElementOf("\"red\"") },
                            ["size"] = new() { Type = "code", Code = JsonElementOf("\"S\"") }
                        }
                    }
                ]
            }
        ]
    };

    private static MetadataV2Resource BatchRuleResource() => ResourceWithRules(new MetadataV2AttributeRule
    {
        Name = "BatchCalc",
        Type = MetadataV2AttributeRuleType.Calculation,
        FieldName = "total",
        ScriptExpression = "$feature.qty * 2",
        Batch = true
    });

    private static MetadataV2Resource InsertOnlyConstraintResource() => ResourceWithRules(new MetadataV2AttributeRule
    {
        Name = "InsertOnly",
        Type = MetadataV2AttributeRuleType.Constraint,
        ScriptExpression = "$feature.qty > 0",
        TriggeringEvents = ["insert"]
    });

    private static MetadataV2Resource ExclusionResource() => ResourceWithRules(new MetadataV2AttributeRule
    {
        Name = "ExcludeDrafts",
        Type = MetadataV2AttributeRuleType.Exclusion,
        ScriptExpression = "$feature.status == 'draft'"
    });

    private static MetadataV2Resource UnsupportedArcadeResource() => ResourceWithRules(new MetadataV2AttributeRule
    {
        Name = "ComplexArcade",
        Type = MetadataV2AttributeRuleType.Constraint,
        ScriptExpression = "Decode($feature.kind, 1, 'a', 'b') == 'a'"
    });

    private static MetadataV2Resource ResourceWithRules(params MetadataV2AttributeRule[] rules)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res", Name = "Rules" },
            AttributeRules = rules
        };

    private static JsonElement JsonElementOf(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, object?> Attributes(params (string Key, object? Value)[] pairs)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }
}
