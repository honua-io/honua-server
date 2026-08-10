// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Turns an operator-selected conflict-resolution action into the concrete feature-store effect that
/// makes the resolution real (#2430). The planner is pure: it reads only the durable conflict record
/// (classification, whether the client edit was committed at sync time, and the captured client/server
/// state envelopes) plus the operator's inputs, and decides whether the resolution needs a write, what
/// state to write, and whether a new committed server state is produced.
/// </summary>
/// <remarks>
/// <para>
/// The key input is <see cref="ReplicaConflictRecord.ClientEditApplied"/>. Under the default
/// last-write-wins conflict-handling mode the conflicting client edit is already committed, so
/// "accept client" is a no-op and "keep server" is the action that needs a write (restoring the
/// captured pre-conflict server state). Under manual review the client edit was skipped, so the
/// polarity flips. Planning both from one record is what keeps the recorded resolution honest against
/// the committed state under either mode.
/// </para>
/// <para>
/// Structural conflicts are deliberately not force-fitted. A client delete that already committed
/// cannot be undone by an update, and a client update cannot be re-applied to a feature the server has
/// deleted; both are rejected as not-applicable with an explicit message rather than recorded as a
/// resolution that changed nothing. The attachment and relationship classifications are not produced by
/// the current replica upload model and are likewise rejected rather than silently accepted.
/// </para>
/// </remarks>
public static class ReplicaConflictResolutionPlanner
{
    private const string AttributesProperty = "attributes";
    private const string GeometryProperty = "geometry";

    /// <summary>Geometry-source token selecting the client's captured geometry.</summary>
    public const string GeometrySourceClient = "client";

    /// <summary>Geometry-source token selecting the server's captured geometry.</summary>
    public const string GeometrySourceServer = "server";

    /// <summary>
    /// Plans the feature-store effect for an operator-selected resolution.
    /// </summary>
    /// <param name="conflict">The durable conflict record being resolved.</param>
    /// <param name="action">The operator-selected resolution action.</param>
    /// <param name="inputs">Operator inputs for merge/geometry actions.</param>
    /// <returns>An accepted plan, or a rejected plan carrying a client-safe reason.</returns>
    public static ReplicaConflictResolutionPlan Plan(
        ReplicaConflictRecord conflict,
        ReplicaConflictResolutionAction action,
        ReplicaConflictResolutionInputs inputs)
    {
        if (action == ReplicaConflictResolutionAction.Defer)
        {
            // Deferral is a review-queue decision, never a write.
            return NoEffect(committedNewServerState: false);
        }

        if (conflict.ConflictType is ReplicaConflictType.Attachment or ReplicaConflictType.Relationship)
        {
            return NotApplicable(
                "Attachment and relationship conflicts cannot be resolved through this surface: the replica upload model does not carry the attachment or related-record inputs a resolution would need.");
        }

        return action switch
        {
            ReplicaConflictResolutionAction.AcceptClient => PlanAcceptClient(conflict),
            ReplicaConflictResolutionAction.KeepServer or ReplicaConflictResolutionAction.RejectClient =>
                PlanKeepServer(conflict),
            ReplicaConflictResolutionAction.MergeFields => PlanMergeFields(conflict, inputs),
            ReplicaConflictResolutionAction.ChooseGeometry => PlanChooseGeometry(conflict, inputs),
            _ => Invalid("Unsupported conflict resolution action."),
        };
    }

    private static ReplicaConflictResolutionPlan PlanAcceptClient(ReplicaConflictRecord conflict)
    {
        if (conflict.ConflictType == ReplicaConflictType.UpdateDelete)
        {
            // The server deleted the feature; there is no row to update and re-creating it would mint a
            // new identity rather than restore the client's target.
            return NotApplicable(
                "The server deleted this feature, so the client update cannot be re-applied. Re-upload the feature as an insert if it should exist again.");
        }

        if (conflict.ClientEditApplied && !conflict.ClientEditOutcomeUnknown)
        {
            // Last-write-wins already committed the client edit; the committed state matches the choice.
            // Skipped when the outcome is unknown: the shortcut asserts the row already holds the client
            // state, and if the ambiguous write never landed this would report the client edit accepted
            // while the server state was still in place. Writing it is idempotent either way (#2430).
            return NoEffect(committedNewServerState: false);
        }

        if (conflict.ConflictType == ReplicaConflictType.DeleteUpdate)
        {
            return new ReplicaConflictResolutionPlan(
                ReplicaConflictResolutionEffect.DeleteFeature,
                FeatureStateJson: null,
                CommittedNewServerState: true,
                ReplicaConflictResolutionRejection.None,
                RejectionMessage: null);
        }

        if (string.IsNullOrWhiteSpace(conflict.ClientStateJson))
        {
            return NotApplicable(
                "No client feature state was captured for this conflict, so the client edit cannot be applied.");
        }

        return Write(conflict.ClientStateJson);
    }

    private static ReplicaConflictResolutionPlan PlanKeepServer(ReplicaConflictRecord conflict)
    {
        if (!conflict.ClientEditApplied && !conflict.ClientEditOutcomeUnknown && !conflict.ClientEditSuperseded)
        {
            // Manual review skipped the client edit, so the server state was never overwritten.
            // Skipped when the outcome is unknown, and when this edit committed but was superseded by a
            // later one from the same upload: in both cases the shortcut's assertion that the row still
            // holds the server state is wrong, and it would report the server state kept while a client
            // overwrite remained. Restoring it is idempotent either way (#2430).
            return NoEffect(committedNewServerState: false);
        }

        if (conflict.ConflictType == ReplicaConflictType.DeleteUpdate)
        {
            return NotApplicable(
                "The client delete has already been committed. Restoring the server feature would require re-inserting it, which conflict resolution does not do; re-import the feature instead.");
        }

        if (conflict.ConflictType == ReplicaConflictType.UpdateDelete)
        {
            // The server deletion stands: a client update against a deleted row never committed.
            return NoEffect(committedNewServerState: false);
        }

        if (string.IsNullOrWhiteSpace(conflict.ServerStateJson))
        {
            return NotApplicable(
                "No pre-conflict server state was captured for this conflict, so the server state cannot be restored.");
        }

        return Write(conflict.ServerStateJson);
    }

    private static ReplicaConflictResolutionPlan PlanMergeFields(
        ReplicaConflictRecord conflict,
        ReplicaConflictResolutionInputs inputs)
    {
        if (inputs.FieldValues is not { Count: > 0 } fieldValues)
        {
            return Invalid("A field merge requires a non-empty 'fieldValues' object of operator-selected attribute values.");
        }

        // Field names are matched to schema fields case-insensitively, so `status` and `STATUS` in one
        // payload name the same field with two values and which one wins depends on dictionary
        // enumeration order. Reject rather than pick: the request does not describe a single state, and
        // an ambiguous merge cannot be reproduced by the resume path either (#2430).
        var duplicate = fieldValues.Keys
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            return Invalid(
                $"'fieldValues' names field '{duplicate.Key}' more than once (field names are case-insensitive); supply a single value per field.");
        }

        if (conflict.ConflictType is ReplicaConflictType.DeleteUpdate or ReplicaConflictType.UpdateDelete)
        {
            return NotApplicable(
                "A field merge does not apply to a delete conflict: choose acceptClient or keepServer instead.");
        }

        // A partial resolution only writes what the operator named and inherits the rest from whichever
        // side is currently committed. Neither captured envelope describes that side when the upload
        // outcome is indeterminate, nor when this edit was superseded by a later one from the same
        // upload whose state was never captured — the merge would silently revert every unmentioned
        // field and the geometry. The whole-state actions stay available (#2430).
        if (conflict.ClientEditOutcomeUnknown || conflict.ClientEditSuperseded)
        {
            return NotApplicable("Which state this feature currently holds is not described by either captured side of this conflict: its uploaded edit either has an unconfirmed commit outcome or was superseded by a later edit in the same upload. A partial resolution would carry the unmentioned attributes and geometry from a side that is not current; choose acceptClient or keepServer, which write a complete known state.");
        }

        // Merge onto the currently committed state where it is known, so unmentioned fields keep their
        // committed values instead of silently reverting to the other side of the conflict.
        var baseEnvelope = conflict.ClientEditApplied
            ? conflict.ClientStateJson ?? conflict.ServerStateJson
            : conflict.ServerStateJson ?? conflict.ClientStateJson;
        if (string.IsNullOrWhiteSpace(baseEnvelope))
        {
            return NotApplicable("No feature state was captured for this conflict, so a field merge has nothing to merge onto.");
        }

        try
        {
            using var document = JsonDocument.Parse(baseEnvelope);
            var merged = WriteEnvelope(
                document.RootElement,
                fieldValues,
                geometrySource: null);
            return Write(merged);
        }
        catch (JsonException)
        {
            return NotApplicable("The captured feature state for this conflict is not valid JSON and cannot be merged.");
        }
    }

    private static ReplicaConflictResolutionPlan PlanChooseGeometry(
        ReplicaConflictRecord conflict,
        ReplicaConflictResolutionInputs inputs)
    {
        var source = inputs.GeometrySource?.Trim().ToLowerInvariant();
        if (source is not (GeometrySourceClient or GeometrySourceServer))
        {
            return Invalid("A geometry choice requires 'geometry' to be either 'client' or 'server'.");
        }

        if (conflict.ConflictType is ReplicaConflictType.DeleteUpdate or ReplicaConflictType.UpdateDelete)
        {
            return NotApplicable(
                "A geometry choice does not apply to a delete conflict: choose acceptClient or keepServer instead.");
        }

        // Same as a field merge: the attributes come from whichever side is currently committed, and
        // neither an indeterminate outcome nor a superseded edit leaves a captured envelope that
        // describes it (#2430).
        if (conflict.ClientEditOutcomeUnknown || conflict.ClientEditSuperseded)
        {
            return NotApplicable("Which state this feature currently holds is not described by either captured side of this conflict: its uploaded edit either has an unconfirmed commit outcome or was superseded by a later edit in the same upload. A partial resolution would carry the unmentioned attributes and geometry from a side that is not current; choose acceptClient or keepServer, which write a complete known state.");
        }

        var chosenEnvelope = source == GeometrySourceClient ? conflict.ClientStateJson : conflict.ServerStateJson;
        if (string.IsNullOrWhiteSpace(chosenEnvelope))
        {
            return NotApplicable($"No {source} feature state was captured for this conflict, so its geometry cannot be chosen.");
        }

        // Attributes come from the currently committed side; only the geometry is taken from the choice.
        var attributeEnvelope = conflict.ClientEditApplied
            ? conflict.ClientStateJson ?? conflict.ServerStateJson
            : conflict.ServerStateJson ?? conflict.ClientStateJson;
        if (string.IsNullOrWhiteSpace(attributeEnvelope))
        {
            return NotApplicable("No feature state was captured for this conflict, so a geometry choice has no attributes to carry.");
        }

        try
        {
            using var attributes = JsonDocument.Parse(attributeEnvelope);
            using var geometry = JsonDocument.Parse(chosenEnvelope);
            if (TryGetGeometry(geometry.RootElement) is not { } chosenGeometry)
            {
                return NotApplicable($"The captured {source} feature state carries no geometry to choose.");
            }

            var resolved = WriteEnvelope(attributes.RootElement, fieldValues: null, geometrySource: chosenGeometry);
            return Write(resolved);
        }
        catch (JsonException)
        {
            return NotApplicable("The captured feature state for this conflict is not valid JSON and cannot be resolved.");
        }
    }

    /// <summary>
    /// Rewrites a state envelope, optionally overriding attribute values and/or the geometry. Attribute
    /// overrides are matched case-insensitively against the existing attribute names (Esri field-name
    /// convention) so an operator-supplied <c>status</c> replaces an existing <c>STATUS</c> rather than
    /// adding a duplicate key the provider would reject.
    /// </summary>
    private static string WriteEnvelope(
        JsonElement envelope,
        IReadOnlyDictionary<string, JsonElement>? fieldValues,
        JsonElement? geometrySource)
    {
        var attributes = TryGetObject(envelope, AttributesProperty);
        var geometry = geometrySource ?? TryGetGeometry(envelope);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(AttributesProperty);
            writer.WriteStartObject();

            var overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (attributes is { } existing)
            {
                foreach (var property in existing.EnumerateObject())
                {
                    if (fieldValues is not null && TryFindOverride(fieldValues, property.Name, out var replacement))
                    {
                        writer.WritePropertyName(property.Name);
                        replacement.WriteTo(writer);
                        overridden.Add(property.Name);
                        continue;
                    }

                    property.WriteTo(writer);
                }
            }

            if (fieldValues is not null)
            {
                foreach (var (name, value) in fieldValues.Where(field => !overridden.Contains(field.Key)))
                {
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();

            if (geometry is { } resolvedGeometry)
            {
                writer.WritePropertyName(GeometryProperty);
                resolvedGeometry.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool TryFindOverride(
        IReadOnlyDictionary<string, JsonElement> fieldValues,
        string name,
        out JsonElement value)
    {
        if (fieldValues.TryGetValue(name, out value))
        {
            return true;
        }

        foreach (var candidate in fieldValues.Where(field => string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase)))
        {
            value = candidate.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static JsonElement? TryGetGeometry(JsonElement envelope)
        => envelope.ValueKind == JsonValueKind.Object &&
            envelope.TryGetProperty(GeometryProperty, out var geometry) &&
            geometry.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? geometry.Clone()
            : null;

    private static JsonElement? TryGetObject(JsonElement envelope, string property)
        => envelope.ValueKind == JsonValueKind.Object &&
            envelope.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static ReplicaConflictResolutionPlan NoEffect(bool committedNewServerState) => new(
        ReplicaConflictResolutionEffect.None,
        FeatureStateJson: null,
        committedNewServerState,
        ReplicaConflictResolutionRejection.None,
        RejectionMessage: null);

    private static ReplicaConflictResolutionPlan Write(string featureStateJson) => new(
        ReplicaConflictResolutionEffect.WriteFeatureState,
        featureStateJson,
        CommittedNewServerState: true,
        ReplicaConflictResolutionRejection.None,
        RejectionMessage: null);

    private static ReplicaConflictResolutionPlan Invalid(string message) => new(
        ReplicaConflictResolutionEffect.None,
        FeatureStateJson: null,
        CommittedNewServerState: false,
        ReplicaConflictResolutionRejection.InvalidRequest,
        message);

    private static ReplicaConflictResolutionPlan NotApplicable(string message) => new(
        ReplicaConflictResolutionEffect.None,
        FeatureStateJson: null,
        CommittedNewServerState: false,
        ReplicaConflictResolutionRejection.NotApplicable,
        message);
}
