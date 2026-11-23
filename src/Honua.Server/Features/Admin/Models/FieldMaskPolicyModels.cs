// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Honua.Server.Features.Admin.Models;

public sealed class FieldMaskPolicyResponse
{
    public required Guid PolicyId { get; init; }

    public required string Role { get; init; }

    public required string Service { get; init; }

    public required string Layer { get; init; }

    public required string Attribute { get; init; }

    public string? Description { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed partial class CreateFieldMaskPolicyRequest : IValidatableObject
{
    private readonly record struct IdentifierRule(string FieldName, string AllowedDescription);

    private const string DisallowedCharacters = "'/', '\\', '<', '>', '{', '}', '\"', '`', ';', or '*'";

    // Policies match IdP role claims, resource metadata names and source field names, all of which
    // may contain spaces and non-ASCII characters, so only separators and markup delimiters are refused.
    [GeneratedRegex(@"\A[^/\\<>{}""`;*]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PolicyNameRegex();

    private static readonly IdentifierRule RoleRule = new(
        nameof(Role),
        $"'*' or a role name without {DisallowedCharacters}");

    private static readonly IdentifierRule ServiceRule = new(
        nameof(Service),
        $"'*' or a service name without {DisallowedCharacters}");

    private static readonly IdentifierRule LayerRule = new(
        nameof(Layer),
        $"'*' or a layer name without {DisallowedCharacters}");

    private static readonly IdentifierRule AttributeRule = new(
        nameof(Attribute),
        $"an attribute name without {DisallowedCharacters}");

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string Role { get; init; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Service { get; init; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Layer { get; init; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Attribute { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ValidateWildcardOrIdentifier(Role, RoleRule) is { } roleError)
        {
            yield return roleError;
        }

        if (ValidateWildcardOrIdentifier(Service, ServiceRule) is { } serviceError)
        {
            yield return serviceError;
        }

        if (ValidateWildcardOrIdentifier(Layer, LayerRule) is { } layerError)
        {
            yield return layerError;
        }

        if (ValidateIdentifier(Attribute, AttributeRule) is { } attributeError)
        {
            yield return attributeError;
        }
    }

    private static ValidationResult? ValidateWildcardOrIdentifier(string? value, IdentifierRule rule)
    {
        if (value is not null && !value.Any(char.IsControl) && value.Trim() == "*")
        {
            return null;
        }

        return ValidateIdentifier(value, rule);
    }

    private static ValidationResult? ValidateIdentifier(string? value, IdentifierRule rule)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Any(char.IsControl))
        {
            return new ValidationResult(
                $"{rule.FieldName} must not contain control characters.",
                [rule.FieldName]);
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return new ValidationResult(
                $"{rule.FieldName} must not be blank.",
                [rule.FieldName]);
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return new ValidationResult(
                $"{rule.FieldName} must not contain path traversal sequences.",
                [rule.FieldName]);
        }

        if (!PolicyNameRegex().IsMatch(trimmed))
        {
            return new ValidationResult(
                $"{rule.FieldName} must be {rule.AllowedDescription}.",
                [rule.FieldName]);
        }

        return null;
    }
}
