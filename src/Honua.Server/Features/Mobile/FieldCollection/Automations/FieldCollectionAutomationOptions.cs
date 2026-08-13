// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Server.Features.Mobile.FieldCollection.Automations;

/// <summary>
/// Configuration for the server-side FieldCollection Workflows companion (#2121).
/// Action definitions are bound from configuration so operators can declare
/// online actions (email/webhook/SMS/assign) without a database migration while
/// the persisted admin CRUD store is built out.
/// </summary>
public sealed class FieldCollectionAutomationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "FieldCollectionAutomation";

    /// <summary>
    /// Gets or sets a value indicating whether automation dispatch is active. When
    /// false the background dispatcher idles and the trigger remains a no-op even
    /// if actions are configured.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the bounded in-process delivery queue capacity. Enqueue waits
    /// for room rather than dropping invocations when the queue is full.
    /// </summary>
    public int QueueCapacity { get; set; } = 1024;

    /// <summary>
    /// Gets or sets the maximum delivery attempts per invocation before it is
    /// abandoned. Retries apply only to retryable handler failures.
    /// </summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Gets the configured action definitions.</summary>
    public IList<FieldCollectionAutomationActionDefinition> Actions { get; set; }
        = new List<FieldCollectionAutomationActionDefinition>();
}

/// <summary>
/// Configuration-bindable form of a
/// <see cref="FieldCollectionAutomationAction"/>. Uses mutable collection types so
/// the options binder can populate it; the store projects it to the immutable
/// domain record.
/// </summary>
public sealed class FieldCollectionAutomationActionDefinition
{
    /// <summary>Gets or sets the stable action identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the action kind.</summary>
    public FieldCollectionAutomationActionType ActionType { get; set; }

    /// <summary>Gets or sets a value indicating whether the action is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the layer scope, or null for any layer.</summary>
    public int? LayerId { get; set; }

    /// <summary>Gets the operations that trigger the action; empty matches all.</summary>
    public IList<FieldCollectionChangeOperation> Operations { get; set; }
        = new List<FieldCollectionChangeOperation>();

    /// <summary>Gets the handler-specific configuration values.</summary>
    public IDictionary<string, string> Configuration { get; set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
