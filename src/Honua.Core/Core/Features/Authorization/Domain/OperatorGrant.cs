namespace Honua.Core.Features.Authorization.Domain;

public sealed record OperatorGrant
{
    public required OperatorResourceType ResourceType { get; init; }

    public required string ResourceId { get; init; }

    public required OperatorOperation Operation { get; init; }

    public const string Wildcard = "*";
}
