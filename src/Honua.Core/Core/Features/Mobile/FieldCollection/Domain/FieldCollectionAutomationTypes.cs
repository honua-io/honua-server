// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Mobile.FieldCollection.Domain;

public enum FieldCollectionAutomationActionType
{
    Email = 1,

    Webhook = 2,

    Sms = 3,

    AssignRecord = 4,
}

public sealed record FieldCollectionAutomationAction
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required FieldCollectionAutomationActionType ActionType { get; init; }

    public bool Enabled { get; init; } = true;

    public int? LayerId { get; init; }

    public ImmutableArray<FieldCollectionChangeOperation> Operations { get; init; }
        = ImmutableArray<FieldCollectionChangeOperation>.Empty;

    public ImmutableDictionary<string, string> Configuration { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}

public sealed record FieldCollectionAutomationEvent
{
    public required string ClientId { get; init; }

    public required string ChangeId { get; init; }

    public required string FeatureId { get; init; }

    public required int LayerId { get; init; }

    public required FieldCollectionChangeOperation Operation { get; init; }

    public required long Version { get; init; }

    public required long Generation { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public string? FeaturePayloadJson { get; init; }
}

public sealed record FieldCollectionActionInvocation
{
    public required string InvocationId { get; init; }

    public required FieldCollectionAutomationAction Action { get; init; }

    public required FieldCollectionAutomationEvent Event { get; init; }

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

public sealed record FieldCollectionActionResult
{
    public required bool Succeeded { get; init; }

    public bool Retryable { get; init; }

    // The error summary never contains a stack, a connection string, or a destination secret.
    public string? Error { get; init; }

    public static FieldCollectionActionResult Success()
        => new() { Succeeded = true };

    public static FieldCollectionActionResult Failure(string error, bool retryable)
        => new() { Succeeded = false, Retryable = retryable, Error = error };
}
