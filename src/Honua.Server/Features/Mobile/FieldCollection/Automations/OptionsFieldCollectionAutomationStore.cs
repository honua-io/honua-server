// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Mobile.FieldCollection.Automations;

/// <summary>
/// Configuration-backed <see cref="IFieldCollectionAutomationStore"/> (#2121).
/// Projects <see cref="FieldCollectionAutomationOptions"/> definitions to immutable
/// domain actions and returns the enabled ones relevant to a layer. A persisted,
/// admin-managed store is a follow-up; this keeps the companion functional from
/// configuration today.
/// </summary>
internal sealed class OptionsFieldCollectionAutomationStore : IFieldCollectionAutomationStore
{
    private readonly IOptionsMonitor<FieldCollectionAutomationOptions> _options;

    public OptionsFieldCollectionAutomationStore(IOptionsMonitor<FieldCollectionAutomationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<IReadOnlyList<FieldCollectionAutomationAction>> GetEnabledActionsAsync(
        int layerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var definitions = _options.CurrentValue.Actions;
        if (definitions is null || definitions.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<FieldCollectionAutomationAction>>(Array.Empty<FieldCollectionAutomationAction>());
        }

        var matched = new List<FieldCollectionAutomationAction>();
        foreach (var definition in definitions)
        {
            if (definition is null || !definition.Enabled)
            {
                continue;
            }

            // Layer-agnostic actions plus actions scoped to this layer. Operation
            // filtering is applied later by the matcher.
            if (definition.LayerId is int scoped && scoped != layerId)
            {
                continue;
            }

            matched.Add(Project(definition));
        }

        return Task.FromResult<IReadOnlyList<FieldCollectionAutomationAction>>(matched);
    }

    private static FieldCollectionAutomationAction Project(FieldCollectionAutomationActionDefinition definition)
        => new()
        {
            Id = definition.Id,
            DisplayName = string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Id : definition.DisplayName,
            ActionType = definition.ActionType,
            Enabled = definition.Enabled,
            LayerId = definition.LayerId,
            Operations = definition.Operations is { Count: > 0 }
                ? definition.Operations.ToImmutableArray()
                : ImmutableArray<FieldCollectionChangeOperation>.Empty,
            Configuration = definition.Configuration is { Count: > 0 }
                ? definition.Configuration.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)
                : ImmutableDictionary<string, string>.Empty,
        };
}
