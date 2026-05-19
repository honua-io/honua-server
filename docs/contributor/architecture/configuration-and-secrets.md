# Configuration and secret management

How Honua loads configuration, resolves secret references, validates options at
startup, and standardizes cache TTLs across features.

Source roots:
- `src/Honua.Core/Configuration/` — validation attributes
- `src/Honua.Core/Features/Configuration/StandardTtlOptions.cs`
- `src/Honua.Core/Features/Security/Abstractions/ISecretProvider.cs`
- `src/Honua.Server/Features/Infrastructure/Configuration/ConfigurationServiceExtensions.cs`

## Components

- **`ISecretProvider`** — abstraction over the configured secret backends
  (Azure Key Vault, AWS Secrets Manager, environment variables). Provides
  caching with a configurable TTL.
- **Validation attributes** in `ConfigurationValidationAttributes.cs` —
  `RequiredConfigurationAttribute`, `ValidUrlAttribute`,
  `SecretReferenceAttribute`, `ValidTtlAttribute`.
- **`StandardTtlOptions`** — five named TTL tiers used across cache layers,
  plus a negative-cache tier for failed lookups.
- **`ConfigurationServiceExtensions`** — DI entry points:
  `AddStandardConfiguration`, `AddConfigurationManagement`,
  `AddSecretManagement`, `AddConfigurationValidation`, and the typed
  `ConfigureWithValidation<TOptions, TValidator>` helper.

## Secret references

Any string-typed option marked with `[SecretReference]` can carry either a
plaintext value (allowed in development) or one of these reference forms:

| Form | Backend |
| --- | --- |
| `env:NAME` | Environment variable |
| `azure:keyvault:<vault>:<secret>` | Azure Key Vault |
| `aws:secretsmanager:<secret-id>` | AWS Secrets Manager |

The provider is selected by prefix. Startup validation tests that the
referenced secret resolves without logging its value; failures fail-fast with
the configuration path and a suggested fix in the error message.

## Validation attributes

```csharp
public sealed class MyServiceOptions
{
    public const string SectionName = "MyService";

    [RequiredConfiguration(
        ConfigurationPath = SectionName,
        SuggestedFix = "Set ApiUrl to your service endpoint")]
    [ValidUrl(RequiredSchemes = new[] { "https" }, RequireHttpsInProduction = true)]
    public string ApiUrl { get; set; } = string.Empty;

    [SecretReference(
        AllowedProviders = new[] { "env", "azure", "aws" },
        AllowPlainTextInDevelopment = true)]
    public string ApiKey { get; set; } = string.Empty;

    [ValidTtl(MinimumTtl = "00:01:00", MaximumTtl = "01:00:00")]
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(15);
}
```

`[ValidUrl]` and `[SecretReference]` apply stricter rules in `Production`
than in `Development` — plaintext secrets and `http://` URLs are rejected in
production but tolerated locally.

## Registering options

```csharp
// Once, early in Program.cs:
builder.Services.AddStandardConfiguration(
    builder.Configuration,
    builder.Environment.IsDevelopment());

// Per options class:
builder.Services.ConfigureWithValidation<MyServiceOptions>(
    builder.Configuration,
    MyServiceOptions.SectionName,
    isRequired: true,
    enableSecretResolution: true);
```

`AddStandardConfiguration` wires the validator, the secret provider, and the
TTL options. `ConfigureWithValidation` binds a section, runs the attribute
validators on startup, and (when `enableSecretResolution: true`) resolves
secret references via `ISecretProvider` before the options are visible to
consumers.

## Standard TTL tiers

`StandardTtlOptions` exposes a fixed set of named TTLs so cache layers stay
consistent. Bind from `StandardTtl` in configuration:

```json
{
  "StandardTtl": {
    "VeryShort": "00:00:30",
    "Short":     "00:05:00",
    "Medium":    "00:30:00",
    "Long":      "04:00:00",
    "VeryLong":  "24:00:00",
    "Negative":  "00:01:00"
  }
}
```

Validation enforces `VeryShort ≤ Short ≤ Medium ≤ Long ≤ VeryLong`. In
`Development`, defaults are scaled down so cache misses surface quickly.

## Environment-variable mapping

ASP.NET Core's `:` → `__` rule applies. Examples:

- `Database:ConnectionString` → `Database__ConnectionString`
- `Cache:Redis:Password` → `Cache__Redis__Password`

`[RequiredConfiguration]` error messages include the env-var form, so
operators see the expected variable name without consulting docs.

## Startup behavior

On successful boot the validator logs a summary line of the form
`Configuration validation completed successfully. Validated <N> sections`.
On failure the application aborts with a message that includes the failing
section, the configuration path, the env-var form, and any `SuggestedFix`
provided on the attribute.
