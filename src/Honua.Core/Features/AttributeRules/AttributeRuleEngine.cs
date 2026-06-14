// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.AttributeRules;

/// <summary>
/// Editing event that can trigger an attribute rule. Mirrors the canonical Esri
/// triggering-event vocabulary (<c>insert</c> / <c>update</c> / <c>delete</c>).
/// </summary>
public enum AttributeRuleEditEvent
{
    /// <summary>A feature is being inserted.</summary>
    Insert,

    /// <summary>An existing feature is being updated.</summary>
    Update,

    /// <summary>A feature is being deleted.</summary>
    Delete
}

/// <summary>
/// A single constraint/validation violation produced while applying attribute rules to
/// a feature on the edit path.
/// </summary>
/// <param name="RuleName">Name of the violated rule.</param>
/// <param name="Message">Operator-facing message describing the violation.</param>
public readonly record struct AttributeRuleViolation(string RuleName, string Message);

/// <summary>
/// Result of applying a resource's attribute rules to one feature's attributes for a
/// given edit event.
/// </summary>
/// <param name="Attributes">
/// The (possibly updated) attribute dictionary after calculation rules have populated
/// their target fields. Always non-null; equals the input map when no calculation rule
/// fired.
/// </param>
/// <param name="Violations">
/// Constraint/validation violations. Empty when every applicable rule passed (or was
/// routed out of scope as unsupported).
/// </param>
public readonly record struct AttributeRuleApplicationResult(
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<AttributeRuleViolation> Violations)
{
    /// <summary>True when no constraint/validation rule was violated.</summary>
    public bool IsValid => Violations.Count == 0;
}

/// <summary>
/// Shared, provider-agnostic engine that applies a resource's
/// <see cref="MetadataV2AttributeRule"/> set to a feature's attributes on the edit path.
/// Calculation rules compute and write their target field before the write; constraint
/// and validation rules evaluate a boolean expression and surface a violation when it is
/// false. Expressions outside the supported safe subset (see
/// <see cref="AttributeRuleExpression"/>) are routed out of scope via the supplied
/// <see cref="IUnsupportedExpressionSink"/> rather than failing the edit, keeping full
/// Arcade parity an explicit non-goal of this path.
/// </summary>
public static class AttributeRuleEngine
{
    /// <summary>
    /// Applies the resource's enabled attribute rules for <paramref name="editEvent"/> to
    /// <paramref name="attributes"/>.
    /// </summary>
    /// <param name="resource">The resource whose attribute rules apply.</param>
    /// <param name="attributes">
    /// Case-insensitive feature attributes. Calculation outputs are written into a copy;
    /// the input map is never mutated.
    /// </param>
    /// <param name="editEvent">The triggering edit event.</param>
    /// <param name="unsupported">
    /// Optional sink notified when a rule's expression is outside the supported subset
    /// (routed out of scope). May be <c>null</c> to silently skip.
    /// </param>
    /// <returns>The application result carrying updated attributes and any violations.</returns>
    public static AttributeRuleApplicationResult Apply(
        MetadataV2Resource resource,
        IReadOnlyDictionary<string, object?> attributes,
        AttributeRuleEditEvent editEvent,
        IUnsupportedExpressionSink? unsupported = null)
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
            if (!rule.IsEnabled || !AppliesTo(rule, editEvent))
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
                                violations ??= new List<AttributeRuleViolation>();
                                violations.Add(new AttributeRuleViolation(
                                    rule.Name,
                                    string.IsNullOrWhiteSpace(rule.ErrorMessage)
                                        ? $"Attribute rule '{rule.Name}' was violated."
                                        : rule.ErrorMessage!));
                                break;
                            case AttributeRuleExpressionStatus.Satisfied:
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

        foreach (var configured in rule.TriggeringEvents)
        {
            if (string.Equals(configured, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Sink notified when an attribute-rule expression falls outside the supported safe
/// subset and is routed out of scope (full Arcade parity is a non-goal of the edit
/// path). Implementations typically log a structured warning identifying the resource
/// and rule.
/// </summary>
public interface IUnsupportedExpressionSink
{
    /// <summary>
    /// Invoked when <paramref name="rule"/> on <paramref name="resource"/> uses an
    /// expression Honua does not evaluate on the edit path.
    /// </summary>
    /// <param name="resource">The resource owning the rule.</param>
    /// <param name="rule">The rule whose expression was unsupported.</param>
    void OnUnsupported(MetadataV2Resource resource, MetadataV2AttributeRule rule);
}
