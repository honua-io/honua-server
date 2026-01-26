// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Admin.Models;

namespace Honua.Admin.Components.Connections;

public sealed class ConnectionFormModel : IValidatableObject
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 5432;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string DatabaseName { get; set; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Username { get; set; } = string.Empty;

    [StringLength(255)]
    public string? Password { get; set; }

    [StringLength(255)]
    public string? SecretReference { get; set; }

    [StringLength(32)]
    public string? SecretType { get; set; }

    public bool SslRequired { get; set; } = true;

    [Required]
    public string SslMode { get; set; } = "Require";

    public bool UseSecretReference { get; set; }

    public bool IsEdit { get; set; }

    public bool CredentialModeLocked { get; set; }

    public Guid? ConnectionId { get; set; }

    public static ConnectionFormModel CreateNew() => new();

    public static ConnectionFormModel FromDetail(SecureConnectionDetail detail)
    {
        var usesSecret = string.Equals(detail.StorageType, "external", StringComparison.OrdinalIgnoreCase);

        return new ConnectionFormModel
        {
            ConnectionId = detail.ConnectionId,
            Name = detail.Name,
            Description = detail.Description,
            Host = detail.Host,
            Port = detail.Port,
            DatabaseName = detail.DatabaseName,
            Username = detail.Username,
            SslRequired = detail.SslRequired,
            SslMode = detail.SslMode,
            UseSecretReference = usesSecret,
            CredentialModeLocked = true,
            SecretReference = detail.CredentialReference,
            IsEdit = true
        };
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SslRequired && string.Equals(SslMode, "Disable", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult(
                "SSL mode cannot be Disable when SSL is required.",
                new[] { nameof(SslMode) });
        }

        if (!IsEdit)
        {
            if (UseSecretReference)
            {
                if (string.IsNullOrWhiteSpace(SecretReference))
                {
                    yield return new ValidationResult(
                        "Secret reference is required.",
                        new[] { nameof(SecretReference) });
                }

                if (string.IsNullOrWhiteSpace(SecretType))
                {
                    yield return new ValidationResult(
                        "Secret type is required.",
                        new[] { nameof(SecretType) });
                }
            }
            else if (string.IsNullOrWhiteSpace(Password))
            {
                yield return new ValidationResult(
                    "Password is required.",
                    new[] { nameof(Password) });
            }
        }
        else if (UseSecretReference && string.IsNullOrWhiteSpace(SecretReference))
        {
            yield return new ValidationResult(
                "Secret reference is required.",
                new[] { nameof(SecretReference) });
        }
    }
}
