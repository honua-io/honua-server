// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.AttributeRules;

public enum AttributeRuleEditEvent
{
    Insert,

    Update,

    Delete
}

public enum AttributeRulePass
{
    Immediate,

    Batch
}

public readonly record struct AttributeRuleViolation(string RuleName, string Message);

public readonly record struct AttributeRuleApplicationResult(
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<AttributeRuleViolation> Violations)
{
    public bool IsValid => Violations.Count == 0;
}

public static class AttributeRuleEngine
{
    public static AttributeRuleApplicationResult Apply(
        MetadataV2Object resource,
        IReadOnlyDictionary<string, object?> attributes,
        AttributeRuleEditEvent editEvent,
        IUnsupportedExpressionSink? unsupported = null,
        AttributeRulePass pass = AttributeRulePass.Immediate)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attributes);

        var rules = resource.AttributeRules;
        if (rules.Count == 0)
        {
            return new AttributeRuleApplicationResult(attributes, Array.Empty<AttributeRuleViolation>());
        }

        Dictionary<string, object?>? working = null;
        List<AttributeRuleViolation>? violations = null;

        // The evaluation snapshot a rule reads from. Calculation rules can feed later
        // rules, so we read from the working copy once one exists.
        IReadOnlyDictionary<string, object?> Current() => working ?? attributes;

        foreach (var rule in rules)
        {
            // Batch rules are deferred to the explicit batch pass; immediate rules run inline.
            // A rule whose triggering event does not match the current edit operation is skipped.
            if (!rule.IsEnabled ||
                rule.Batch != (pass == AttributeRulePass.Batch) ||
                !AppliesTo(rule, editEvent))
            {
                continue;
            }

            switch (rule.Type)
            {
                case MetadataV2AttributeRuleType.Calculation:
                    {
                        if (string.IsNullOrWhiteSpace(rule.FieldName))
                        {
                            continue;
                        }

                        var result = AttributeRuleExpression.Evaluate(rule.ScriptExpression, Current(), expectBoolean: false);
                        if (result.Status == AttributeRuleExpressionStatus.Unsupported)
                        {
                            unsupported?.OnUnsupported(resource, rule);
                            continue;
                        }

                        working ??= new Dictionary<string, object?>(attributes, StringComparer.OrdinalIgnoreCase);
                        working[rule.FieldName] = result.Value;
                        break;
                    }

                case MetadataV2AttributeRuleType.Constraint:
                case MetadataV2AttributeRuleType.Validation:
                    {
                        var result = AttributeRuleExpression.Evaluate(rule.ScriptExpression, Current(), expectBoolean: true);
                        switch (result.Status)
                        {
                            case AttributeRuleExpressionStatus.Unsupported:
                                unsupported?.OnUnsupported(resource, rule);
                                break;
                            case AttributeRuleExpressionStatus.Violated:
                                AddViolation(ref violations, rule);
                                break;
                            case AttributeRuleExpressionStatus.Satisfied:
                            case AttributeRuleExpressionStatus.Computed:
                            default:
                                break;
                        }

                        break;
                    }

                case MetadataV2AttributeRuleType.Exclusion:
                    {
                        // Exclusion is the inverse of a constraint: a satisfied (true) boolean
                        // means the edit matches the exclusion condition and must be aborted.
                        var result = AttributeRuleExpression.Evaluate(rule.ScriptExpression, Current(), expectBoolean: true);
                        switch (result.Status)
                        {
                            case AttributeRuleExpressionStatus.Unsupported:
                                unsupported?.OnUnsupported(resource, rule);
                                break;
                            case AttributeRuleExpressionStatus.Satisfied:
                                AddViolation(ref violations, rule);
                                break;
                            case AttributeRuleExpressionStatus.Violated:
                            case AttributeRuleExpressionStatus.Computed:
                            default:
                                break;
                        }

                        break;
                    }

                default:
                    break;
            }
        }

        return new AttributeRuleApplicationResult(
            working ?? attributes,
            (IReadOnlyList<AttributeRuleViolation>?)violations ?? Array.Empty<AttributeRuleViolation>());
    }

    private static void AddViolation(ref List<AttributeRuleViolation>? violations, MetadataV2AttributeRule rule)
    {
        violations ??= new List<AttributeRuleViolation>();
        violations.Add(new AttributeRuleViolation(
            rule.Name,
            string.IsNullOrWhiteSpace(rule.ErrorMessage)
                ? $"Attribute rule '{rule.Name}' was violated."
                : rule.ErrorMessage!));
    }

    private static bool AppliesTo(MetadataV2AttributeRule rule, AttributeRuleEditEvent editEvent)
    {
        // An empty triggering-events set fires on every editing event (Esri default).
        if (rule.TriggeringEvents.Count == 0)
        {
            return editEvent != AttributeRuleEditEvent.Delete || rule.Type != MetadataV2AttributeRuleType.Calculation;
        }

        var token = editEvent switch
        {
            AttributeRuleEditEvent.Insert => "insert",
            AttributeRuleEditEvent.Update => "update",
            AttributeRuleEditEvent.Delete => "delete",
            _ => string.Empty
        };

        return rule.TriggeringEvents.Any(configured => string.Equals(configured, token, StringComparison.OrdinalIgnoreCase));
    }
}

public interface IUnsupportedExpressionSink
{
    void OnUnsupported(MetadataV2Object resource, MetadataV2AttributeRule rule);
}
