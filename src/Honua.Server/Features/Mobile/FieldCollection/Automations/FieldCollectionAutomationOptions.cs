// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Server.Features.Mobile.FieldCollection.Automations;

public sealed class FieldCollectionAutomationOptions
{
    public const string SectionName = "FieldCollectionAutomation";

    public bool Enabled { get; set; }

    public int QueueCapacity { get; set; } = 1024;

    public int MaxAttempts { get; set; } = 4;

    public IList<FieldCollectionAutomationActionDefinition> Actions { get; set; }
        = new List<FieldCollectionAutomationActionDefinition>();
}

public sealed class FieldCollectionAutomationActionDefinition
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public FieldCollectionAutomationActionType ActionType { get; set; }

    public bool Enabled { get; set; } = true;

    public int? LayerId { get; set; }

    public IList<FieldCollectionChangeOperation> Operations { get; set; }
        = new List<FieldCollectionChangeOperation>();

    public IDictionary<string, string> Configuration { get; set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
