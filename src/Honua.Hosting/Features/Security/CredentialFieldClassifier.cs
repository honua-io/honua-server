// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Classifies JSON/object field names that carry raw credential material rather than
/// a reference, identifier, prefix, type, or aggregate status about a credential.
/// </summary>
internal static class CredentialFieldClassifier
{
    public static bool IsRawCredential(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        var normalized = new string(propertyName
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized is "password"
            or "clientsecret"
            or "apikey"
            or "honuaapikey"
            or "accesskey"
            or "accesstoken"
            or "refreshtoken"
            or "idtoken"
            or "token"
            or "privatekey"
            or "keymaterial"
            or "connectionstring"
            or "credential"
            or "key"
            or "secret";
    }

    public static bool ContainsRawCredential(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (IsRawCredential(property.Name)
                    || ContainsRawCredential(property.Value))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (ContainsRawCredential(item))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            return ContainsRawCredential(value.GetString());
        }

        return false;
    }

    public static bool ContainsRawCredential(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || (value[0] != '{' && value[0] != '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return ContainsRawCredential(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
