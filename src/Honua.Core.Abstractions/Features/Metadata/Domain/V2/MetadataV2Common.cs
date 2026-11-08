// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2Constants
{
    public const string SchemaVersion = "2.0.0-alpha.1";

    public const string ApiVersion = "metadata.honua.io/v2alpha1";
}

public sealed record MetadataV2ObjectMetadata : ExtensibleJsonRecord
{
    public const int MaxAttributionLength = 512;

    public const int MaxPublisherLength = 256;

    private IReadOnlyList<string>? _tags;
    private IReadOnlyDictionary<string, string>? _labels;
    private IReadOnlyDictionary<string, string>? _annotations;
    private IReadOnlyList<string>? _keywords;
    private IReadOnlyList<string>? _themes;
    private IReadOnlyList<MetadataV2Link>? _links;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    // A tenant mismatch is treated as not found so one tenant cannot discover another's collections.
    [JsonPropertyName("tenant")]
    public string? Tenant { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags
    {
        get => _tags ?? Array.Empty<string>();
        init => _tags = value;
    }

    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string> Labels
    {
        get => _labels ?? EmptyStringMap;
        init => _labels = value;
    }

    [JsonPropertyName("annotations")]
    public IReadOnlyDictionary<string, string> Annotations
    {
        get => _annotations ?? EmptyStringMap;
        init => _annotations = value;
    }

    [JsonPropertyName("generation")]
    public long? Generation { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("keywords")]
    public IReadOnlyList<string> Keywords
    {
        get => _keywords ?? Array.Empty<string>();
        init => _keywords = value;
    }

    [JsonPropertyName("themes")]
    public IReadOnlyList<string> Themes
    {
        get => _themes ?? Array.Empty<string>();
        init => _themes = value;
    }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("attribution")]
    public string? Attribution { get; init; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    [JsonPropertyName("contactPoint")]
    public MetadataV2ContactPoint? ContactPoint { get; init; }

    [JsonPropertyName("links")]
    public IReadOnlyList<MetadataV2Link> Links
    {
        get => _links ?? Array.Empty<MetadataV2Link>();
        init => _links = value;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyStringMap =
        new Dictionary<string, string>();
}

[JsonConverter(typeof(MetadataV2StatusJsonConverter))]
public sealed record MetadataV2Status : ExtensibleJsonRecord
{
    [JsonPropertyName("lifecycle")]
    public MetadataV2LifecycleStatus Lifecycle { get; init; } = MetadataV2LifecycleStatus.Active;

    [JsonPropertyName("state")]
    public MetadataV2OperationalState State { get; init; } = MetadataV2OperationalState.Unknown;

    [JsonPropertyName("conditions")]
    public IReadOnlyList<MetadataV2Condition> Conditions { get; init; } = Array.Empty<MetadataV2Condition>();

    [JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; init; }
}

public sealed record MetadataV2Condition : ExtensibleJsonRecord
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("lastTransitionAt")]
    public DateTimeOffset? LastTransitionAt { get; init; }
}

public sealed record MetadataV2ExtensionPoint : ExtensibleJsonRecord
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("appliesTo")]
    public IReadOnlyList<string> AppliesTo { get; init; } = Array.Empty<string>();

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; init; }
}

public sealed record MetadataV2ContactPoint : ExtensibleJsonRecord
{
    public const int MaxNameLength = 256;

    public const int MaxUrlLength = MetadataV2Link.MaxGovernanceHrefLength;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed record MetadataV2Link : ExtensibleJsonRecord
{
    public const int MaxGovernanceHrefLength = 2048;

    [JsonPropertyName("href")]
    public string Href { get; init; } = string.Empty;

    [JsonPropertyName("rel")]
    public string Rel { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("hreflang")]
    public string? Hreflang { get; init; }

    [JsonPropertyName("managedBy")]
    public string? ManagedBy { get; init; }
}

public sealed record MetadataV2Field : ExtensibleJsonRecord
{
    private IReadOnlyList<string>? _semanticRoles;

    [JsonPropertyName("semanticId")]
    public string? SemanticId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public MetadataV2FieldType Type { get; init; } = MetadataV2FieldType.Unknown;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; }

    [JsonPropertyName("semanticRoles")]
    public IReadOnlyList<string> SemanticRoles
    {
        get => _semanticRoles ?? Array.Empty<string>();
        init => _semanticRoles = value;
    }

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    // Nullable storage preserves the true editable default when the JSON member is omitted, because source-generated deserialization materializes an absent value type as its CLR default.
    [JsonPropertyName("editable")]
    public bool? EditableValue { get; init; }

    [JsonIgnore]
    public bool Editable
    {
        get => EditableValue ?? true;
        init => EditableValue = value;
    }

    [JsonPropertyName("length")]
    public int? Length { get; init; }

    [JsonPropertyName("defaultValue")]
    public JsonElement? DefaultValue { get; init; }

    [JsonPropertyName("domain")]
    public MetadataV2FieldDomain? Domain { get; init; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; init; }

    [JsonPropertyName("sqlType")]
    public string? SqlType { get; init; }

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2FieldDomain : ExtensibleJsonRecord
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("codedValues")]
    public IReadOnlyList<MetadataV2CodedValue> CodedValues { get; init; } = Array.Empty<MetadataV2CodedValue>();

    [JsonPropertyName("range")]
    public IReadOnlyList<JsonElement>? Range { get; init; }

    [JsonPropertyName("mergePolicy")]
    public string? MergePolicy { get; init; }

    [JsonPropertyName("splitPolicy")]
    public string? SplitPolicy { get; init; }
}

public sealed record MetadataV2CodedValue : ExtensibleJsonRecord
{
    [JsonPropertyName("code")]
    public JsonElement Code { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed record MetadataV2Subtypes : ExtensibleJsonRecord
{
    [JsonPropertyName("subtypeField")]
    public string SubtypeField { get; init; } = string.Empty;

    [JsonPropertyName("defaultSubtypeCode")]
    public JsonElement? DefaultSubtypeCode { get; init; }

    [JsonPropertyName("subtypes")]
    public IReadOnlyList<MetadataV2Subtype> Subtypes { get; init; } = Array.Empty<MetadataV2Subtype>();
}

public sealed record MetadataV2Subtype : ExtensibleJsonRecord
{
    [JsonPropertyName("code")]
    public JsonElement Code { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("fieldOverrides")]
    public IReadOnlyDictionary<string, MetadataV2SubtypeFieldOverride> FieldOverrides { get; init; }
        = new Dictionary<string, MetadataV2SubtypeFieldOverride>(StringComparer.OrdinalIgnoreCase);
}

public sealed record MetadataV2SubtypeFieldOverride : ExtensibleJsonRecord
{
    private JsonElement? _defaultValue;
    private static readonly JsonElement ExplicitNull = CreateExplicitNull();

    [JsonPropertyName("defaultValueIsNull")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DefaultValueIsNull { get; init; }

    [JsonPropertyName("defaultValue")]
    public JsonElement? DefaultValue
    {
        get => _defaultValue ?? (DefaultValueIsNull ? ExplicitNull : null);
        init => _defaultValue = value;
    }

    [JsonPropertyName("domain")]
    public MetadataV2FieldDomain? Domain { get; init; }

    private static JsonElement CreateExplicitNull()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}

public sealed record MetadataV2ContingentValueGroup : ExtensibleJsonRecord
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("restrictive")]
    public bool Restrictive { get; init; } = true;

    [JsonPropertyName("fields")]
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    [JsonPropertyName("contingentValues")]
    public IReadOnlyList<MetadataV2ContingentValue> ContingentValues { get; init; }
        = Array.Empty<MetadataV2ContingentValue>();
}

public sealed record MetadataV2ContingentValue : ExtensibleJsonRecord
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("subtypeCode")]
    public JsonElement? SubtypeCode { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyDictionary<string, MetadataV2ContingentFieldValue> Values { get; init; }
        = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase);
}

public sealed record MetadataV2ContingentFieldValue : ExtensibleJsonRecord
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "any";

    [JsonPropertyName("code")]
    public JsonElement? Code { get; init; }

    [JsonPropertyName("range")]
    public IReadOnlyList<JsonElement>? Range { get; init; }
}

// An Arcade expression outside the supported subset is skipped rather than failing the edit.
public sealed record MetadataV2AttributeRule : ExtensibleJsonRecord
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public MetadataV2AttributeRuleType Type { get; init; } = MetadataV2AttributeRuleType.Calculation;

    [JsonPropertyName("fieldName")]
    public string? FieldName { get; init; }

    [JsonPropertyName("scriptExpression")]
    public string ScriptExpression { get; init; } = string.Empty;

    [JsonPropertyName("triggeringEvents")]
    public IReadOnlyList<string> TriggeringEvents { get; init; } = Array.Empty<string>();

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; init; } = true;

    [JsonPropertyName("batch")]
    public bool Batch { get; init; }
}

public enum MetadataV2AttributeRuleType
{
    Calculation,

    Constraint,

    Validation,

    Exclusion,

    Unknown = -1
}

public sealed record MetadataV2OwnerEditPolicy : ExtensibleJsonRecord
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("ownerField")]
    public string OwnerField { get; init; } = string.Empty;

    [JsonPropertyName("stampOwnerOnInsert")]
    public bool StampOwnerOnInsert { get; init; } = true;
}
