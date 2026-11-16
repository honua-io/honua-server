using System.Text.Json.Serialization;

namespace Honua.Core.Features.Authorization.Domain;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OperatorPolicyCatalog))]
[JsonSerializable(typeof(ApprovalRequirement))]
[JsonSerializable(typeof(OperatorGrant))]
[JsonSerializable(typeof(OperatorResourceType))]
[JsonSerializable(typeof(OperatorOperation))]
[JsonSerializable(typeof(WorkspaceVisibility))]
[JsonSerializable(typeof(Dictionary<OperatorResourceType, IReadOnlyList<OperatorOperation>>),
    TypeInfoPropertyName = "AllowedOperationsDict")]
public sealed partial class OperatorAuthorizationJsonContext : JsonSerializerContext;
