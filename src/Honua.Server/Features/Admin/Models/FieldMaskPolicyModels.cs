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
    private readonly record struct IdentifierRule(string FieldName, Regex Pattern, string AllowedDescription);

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9._:-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleIdentifierRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeIdentifierRegex();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeIdentifierRegex();

    private static readonly IdentifierRule RoleRule = new(
        nameof(Role),
        RoleIdentifierRegex(),
        "'*' or a role identifier using letters, digits, '.', '_', '-', or ':'");

    private static readonly IdentifierRule ServiceRule = new(
        nameof(Service),
        ScopeIdentifierRegex(),
        "'*' or a service identifier using letters, digits, '.', '_', or '-'");

    private static readonly IdentifierRule LayerRule = new(
        nameof(Layer),
        ScopeIdentifierRegex(),
        "'*' or a layer identifier using letters, digits, '.', '_', or '-'");

    private static readonly IdentifierRule AttributeRule = new(
        nameof(Attribute),
        AttributeIdentifierRegex(),
        "an attribute identifier starting with a letter or '_' and using letters, digits, '.', '_', or '-'");

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

        if (!rule.Pattern.IsMatch(trimmed))
        {
            return new ValidationResult(
                $"{rule.FieldName} must be {rule.AllowedDescription}.",
                [rule.FieldName]);
        }

        return null;
    }
}
