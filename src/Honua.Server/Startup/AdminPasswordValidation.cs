// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Startup;

/// <summary>
/// Shared production validation for both directly configured and secret-backed admin passwords.
/// </summary>
internal static class AdminPasswordValidation
{
    public static void ValidateProductionPassword(string adminPassword)
    {
        if (adminPassword.Length < 16)
        {
            throw new InvalidOperationException(
                "Admin password must be at least 16 characters in production environment");
        }

        var hasUpper = adminPassword.Any(char.IsUpper);
        var hasLower = adminPassword.Any(char.IsLower);
        var hasDigit = adminPassword.Any(char.IsDigit);
        var hasSpecial = adminPassword.Any(static character => !char.IsLetterOrDigit(character));

        if (!(hasUpper && hasLower && hasDigit && hasSpecial))
        {
            throw new InvalidOperationException(
                "Admin password must contain uppercase, lowercase, digit, and special characters in production environment");
        }
    }

    public static bool IsAwsSecretsManagerReference(string? value) =>
        value?.StartsWith("aws:secretsmanager:", StringComparison.OrdinalIgnoreCase) == true;
}
