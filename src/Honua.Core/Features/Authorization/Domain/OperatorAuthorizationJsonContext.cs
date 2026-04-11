using System.Text.Json.Serialization;

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Source-generated JSON serialization context for operator authorization domain models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OperatorPolicyCatalog))]
[JsonSerializable(typeof(ApprovalRequirement))]
[JsonSerializable(typeof(OperatorPermission))]
[JsonSerializable(typeof(OperatorResourceType))]
[JsonSerializable(typeof(OperatorOperation))]
[JsonSerializable(typeof(WorkspaceVisibility))]
[JsonSerializable(typeof(Dictionary<OperatorResourceType, IReadOnlyList<OperatorOperation>>),
    TypeInfoPropertyName = "AllowedOperationsDict")]
public sealed partial class OperatorAuthorizationJsonContext : JsonSerializerContext;
