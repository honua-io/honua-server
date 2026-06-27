// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Mobile.FieldCollection.Domain;

/// <summary>
/// Online action kinds the server-side Workflows companion can run when a
/// FieldCollection record syncs up from a device (#2121). Pairs with the
/// device-side offline automation runtime shipped in <c>Honua.Collect.Core</c>:
/// offline-queued HTTP replays run on the device, while these online actions run
/// server-side once the change is durably applied.
/// </summary>
public enum FieldCollectionAutomationActionType
{
    /// <summary>Send an email notification.</summary>
    Email = 1,

    /// <summary>POST a signed JSON payload to an HTTPS webhook endpoint.</summary>
    Webhook = 2,

    /// <summary>Send an SMS notification.</summary>
    Sms = 3,

    /// <summary>Assign the synced record to a user or queue.</summary>
    AssignRecord = 4,
}

/// <summary>
/// A configured server-side automation action. Definitions are persisted by an
/// <see cref="Abstractions.IFieldCollectionAutomationStore"/> and matched against
/// applied changes by <see cref="FieldCollectionAutomationMatcher"/>.
/// </summary>
public sealed record FieldCollectionAutomationAction
{
    /// <summary>Gets the stable identifier of this action definition.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the human-readable name shown in admin tooling.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the kind of online action to perform.</summary>
    public required FieldCollectionAutomationActionType ActionType { get; init; }

    /// <summary>
    /// Gets a value indicating whether the action is active. Disabled actions are
    /// never matched and never dispatched.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets the layer the action is scoped to. <see langword="null"/> matches
    /// changes on any layer.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Gets the change operations that trigger the action. An empty set matches
    /// every operation (insert, update, delete).
    /// </summary>
    public ImmutableArray<FieldCollectionChangeOperation> Operations { get; init; }
        = ImmutableArray<FieldCollectionChangeOperation>.Empty;

    /// <summary>
    /// Gets handler-specific configuration (for example <c>url</c> and <c>secret</c>
    /// for a webhook, <c>recipients</c> for email/SMS, or <c>field</c> and
    /// <c>value</c> for record assignment). Kept as a string map so definitions
    /// round-trip through configuration and persistence without bespoke schemas
    /// per action type.
    /// </summary>
    public ImmutableDictionary<string, string> Configuration { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}

/// <summary>
/// The applied-change signal that drives server-side automation. Produced after a
/// FieldCollection push is durably applied so that actions never fire for
/// conflicts or rejected writes.
/// </summary>
public sealed record FieldCollectionAutomationEvent
{
    /// <summary>Gets the identifier of the client (device) that pushed the change.</summary>
    public required string ClientId { get; init; }

    /// <summary>Gets the identifier of the change the client assigned.</summary>
    public required string ChangeId { get; init; }

    /// <summary>Gets the identifier of the feature affected by the change.</summary>
    public required string FeatureId { get; init; }

    /// <summary>Gets the identifier of the layer that contains the feature.</summary>
    public required int LayerId { get; init; }

    /// <summary>Gets the operation that was applied.</summary>
    public required FieldCollectionChangeOperation Operation { get; init; }

    /// <summary>Gets the server-assigned version after the change was applied.</summary>
    public required long Version { get; init; }

    /// <summary>Gets the server generation the applied change belongs to.</summary>
    public required long Generation { get; init; }

    /// <summary>Gets the timestamp at which the change was recorded.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Pre-serialized JSON payload of the applied feature. Null for delete
    /// operations, mirroring the push contract.
    /// </summary>
    public string? FeaturePayloadJson { get; init; }
}

/// <summary>
/// A single matched (action, event) pair queued for delivery. The invocation id
/// is a stable idempotency key derived from the change and action so a handler
/// can dedupe retried deliveries.
/// </summary>
public sealed record FieldCollectionActionInvocation
{
    /// <summary>Gets the stable idempotency key for this invocation.</summary>
    public required string InvocationId { get; init; }

    /// <summary>Gets the action definition to run.</summary>
    public required FieldCollectionAutomationAction Action { get; init; }

    /// <summary>Gets the applied-change event that triggered the action.</summary>
    public required FieldCollectionAutomationEvent Event { get; init; }

    /// <summary>
    /// Builds an invocation with a deterministic idempotency key derived from the
    /// triggering change and the action. Replaying the same applied change for the
    /// same action yields the same key.
    /// </summary>
    public static FieldCollectionActionInvocation Create(
        FieldCollectionAutomationAction action,
        FieldCollectionAutomationEvent automationEvent)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(automationEvent);

        return new FieldCollectionActionInvocation
        {
            InvocationId = $"{automationEvent.ClientId}:{automationEvent.ChangeId}:{action.Id}",
            Action = action,
            Event = automationEvent,
        };
    }
}

/// <summary>
/// Outcome returned by a <see cref="Abstractions.IFieldCollectionActionHandler"/>.
/// </summary>
public sealed record FieldCollectionActionResult
{
    /// <summary>Gets a value indicating whether the action delivered successfully.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Gets a value indicating whether a failed delivery is worth retrying.
    /// Ignored when <see cref="Succeeded"/> is <see langword="true"/>.
    /// </summary>
    public bool Retryable { get; init; }

    /// <summary>
    /// Gets an optional, redacted error summary. Never contains stack traces,
    /// connection strings, or destination secrets.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static FieldCollectionActionResult Success()
        => new() { Succeeded = true };

    /// <summary>Creates a failed result.</summary>
    public static FieldCollectionActionResult Failure(string error, bool retryable)
        => new() { Succeeded = false, Retryable = retryable, Error = error };
}
