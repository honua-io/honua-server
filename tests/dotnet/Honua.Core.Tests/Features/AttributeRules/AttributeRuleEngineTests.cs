// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Tests.Features.AttributeRules;

/// <summary>
/// Unit coverage for the shared attribute-rule engine and safe Arcade-subset evaluator
/// (honua-server#1271). Verifies that calculation rules populate their target field,
/// constraint/validation rules reject violations, triggering events gate firing, and
/// expressions outside the supported subset are routed out of scope (skipped) rather
/// than failing the edit.
/// </summary>
public sealed class AttributeRuleEngineTests
{
    [Fact]
    public void Apply_CalculationConstantRule_PopulatesTargetField()
    {
        var resource = ResourceWithRules(new MetadataV2AttributeRule
        {
            Name = "SetStatus",
            Type = MetadataV2AttributeRuleType.Calculation,
            FieldName = "status",
            ScriptExpression = "return 'active';"
        });

        var result = AttributeRuleEngine.Apply(
            resource,
            Attributes(("qty", 5)),
            AttributeRuleEditEvent.Insert);

        result.IsValid.Should().BeTrue();
        result.Attributes["status"].Should().Be("active");
        result.Attributes["qty"].Should().Be(5);
    }

    [Fact]
    public void Apply_CalculationArithmeticRule_ComputesFromFields()
    {
        var resource = ResourceWithRules(new MetadataV2AttributeRule
        {
            Name = "Total",
            Type = MetadataV2AttributeRuleType.Calculation,
            FieldName = "total",
            ScriptExpression = "$feature.qty * $feature.price"
        });

        var result = AttributeRuleEngine.Apply(
            resource,
            Attributes(("qty", 3), ("price", 4)),
            AttributeRuleEditEvent.Update);

        result.IsValid.Should().BeTrue();
        result.Attributes["total"].Should().Be(12d);
    }

    [Fact]
    public void Apply_ConstraintViolated_ReportsViolationWithMessage()
    {
        var resource = ResourceWithRules(new MetadataV2AttributeRule
        {
            Name = "PositiveQty",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "$feature.qty > 0",
            ErrorMessage = "Quantity must be positive"
        });

        var result = AttributeRuleEngine.Apply(
            resource,
            Attributes(("qty", -1)),
            AttributeRuleEditEvent.Insert);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainSingle();
        result.Violations[0].Message.Should().Be("Quantity must be positive");
    }

    [Fact]
    public void Apply_ConstraintSatisfied_NoViolation()
    {
        var resource = ResourceWithRules(new MetadataV2AttributeRule
        {
            Name = "PositiveQty",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "$feature.qty > 0"
        });

        var result = AttributeRuleEngine.Apply(
            resource,
            Attributes(("qty", 10)),
            AttributeRuleEditEvent.Insert);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Apply_TriggeringEventMismatch_RuleDoesNotFire()
    {
        var resource = ResourceWithRules(new MetadataV2AttributeRule
        {
            Name = "InsertOnly",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "$feature.qty > 0",
            TriggeringEvents = ["insert"]
        });

        // An update-time edit must not fire an insert-only rule even when it would violate.
        var result = AttributeRuleEngine.Apply(
            resource,
            Attributes(("qty", -5)),
            AttributeRuleEditEvent.Update);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Apply_DisabledRule_DoesNotFire()
    {
        var resource = ResourceWithRules(new MetadataV2AttributeRule
        {
            Name = "Off",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "$feature.qty > 0",
            IsEnabled = false
        });

        var result = AttributeRuleEngine.Apply(
            resource,
            Attributes(("qty", -5)),
            AttributeRuleEditEvent.Insert);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Apply_UnsupportedExpression_RoutedOutOfScopeNotFailed()
    {
        var resource = ResourceWithRules(new MetadataV2AttributeRule
        {
            Name = "ComplexArcade",
            Type = MetadataV2AttributeRuleType.Constraint,
            // A function call is outside the supported safe subset.
            ScriptExpression = "Count($feature.parts) > 0"
        });

        var sink = new RecordingSink();
        var result = AttributeRuleEngine.Apply(
            resource,
            Attributes(("qty", 1)),
            AttributeRuleEditEvent.Insert,
            sink);

        // Unsupported expressions skip rather than fail the edit, and notify the sink.
        result.IsValid.Should().BeTrue();
        sink.Unsupported.Should().ContainSingle().Which.Should().Be("ComplexArcade");
    }

    [Fact]
    public void Apply_UnsupportedCalculation_DoesNotWriteTargetField()
    {
        var resource = ResourceWithRules(new MetadataV2AttributeRule
        {
            Name = "ComplexCalc",
            Type = MetadataV2AttributeRuleType.Calculation,
            FieldName = "status",
            ScriptExpression = "DomainName($feature, 'status')"
        });

        var result = AttributeRuleEngine.Apply(
            resource,
            Attributes(("qty", 1)),
            AttributeRuleEditEvent.Insert);

        result.IsValid.Should().BeTrue();
        result.Attributes.ContainsKey("status").Should().BeFalse();
    }

    private static MetadataV2Resource ResourceWithRules(params MetadataV2AttributeRule[] rules)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res", Name = "Test" },
            AttributeRules = rules
        };

    private static Dictionary<string, object?> Attributes(params (string Key, object? Value)[] pairs)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    private sealed class RecordingSink : IUnsupportedExpressionSink
    {
        public List<string> Unsupported { get; } = new();

        public void OnUnsupported(MetadataV2Resource resource, MetadataV2AttributeRule rule)
            => Unsupported.Add(rule.Name);
    }
}
