# Azure AI + License DRAFT notes (epic #1748 — #1744 / #1745)

DRAFT mirroring AWS work NOT yet on trunk:
- #1744 Azure OpenAI provider mirrors PR #1737 `feat/ai-studio-bedrock-provider` (open).
- #1745 Azure Key Vault license resolver mirrors issue #1742 (no PR, owns `ILicenseContentSecretResolver`).

Rework/collision risk is explicitly accepted. This file is a scratch note for the draft and
should be removed (or folded into an ADR) when the work is finalized.

## STEP 0 — cloud-SDK isolation rule (the binding constraint)

Two architecture guards enforce isolation; both MUST stay green:

### `CloudSdkIsolationTests`
- `BannedPackagePrefixes = { "AWSSDK.", "Azure." }`.
- **Cloud-neutral projects** — `Honua.Core.Abstractions`, `Honua.Core`, `Honua.Hosting` — must
  have a *transitive* PackageReference closure with **no** `AWSSDK.*` / `Azure.*` package.
  (Microsoft.Extensions.AI / .AI.Abstractions are NOT banned — they are cloud-neutral.)
- `Honua.Server` source must not contain `using Amazon.*` / `using Azure.*` (or `using static`).
  SDK-typed code must live in `Honua.Aws` / `Honua.Azure`, which keep the
  `Honua.Server.Features.*` namespaces so callers don't need a using sweep.

### `ModuleDependencyPolicyTests._allowedCells` (the ProjectReference matrix)
Relevant cells (exhaustive — anything not listed FAILS):
- `Honua.Azure` may reference: Abstractions, Core, **Hosting**, Jobs, ServiceDefaults. NOT Ai.
- `Honua.Ai` may reference: Abstractions, Core, Geocoding, Routing, **Hosting**, Jobs,
  Geoprocessing, ServiceDefaults. NOT Azure. (`HonuaAiIsolationTests` also forbids Ai -> Server.)
- `Honua.Server` references both Ai and Azure (Azure is conditional on `HonuaIncludeAzure=true`).

### Which assemblies may reference `Azure.*` / `AWSSDK.*`
- `Azure.*` SDK packages: `Honua.Azure` (and `Honua.Postgres` for the connection-secret
  Key Vault resolver, plus `Honua.Geocoding` for Azure Maps). NOT Core/Abstractions/Hosting/Server/Ai.
- `AWSSDK.*`: `Honua.Aws` (+ Geocoding LocationService). NOTE: PR #1737 puts
  `AWSSDK.BedrockRuntime` and `Amazon.*`-typed code directly in **`Honua.Ai`** — that leaks the
  AWS SDK into the cloud-neutral-ish AI surface. The current arch tests do NOT catch it because
  `Honua.Ai` is not in `CloudSdkIsolationTests.CloudNeutralProjects` and the matrix test only
  checks ProjectReferences (not PackageReferences). It is still an isolation smell.

## Placement decisions (Azure code placed CORRECTLY regardless of #1737's choice)

### #1744 Azure OpenAI provider — split across 3 assemblies
The Bedrock provider reuses `Honua.Ai`-internal helpers (`WorkflowGenerationSchema`,
`WorkflowGenerationPrompt`, `WorkflowGenerationProposalMapper`, `WorkflowGenerationModelProposal`,
`WorkflowGenerationJsonContext`) — all `internal` to `Honua.Ai`. So the *provider* must stay in
`Honua.Ai`. But the Azure SDK (`Azure.AI.OpenAI`) typed code must NOT be in `Honua.Ai`. Resolution:

- **`Honua.Hosting`** — `IAzureOpenAiChatClientFactory` (cloud-neutral seam; returns
  `Microsoft.Extensions.AI.IChatClient`, no `Azure.*` types in its signature). Hosting is the only
  assembly BOTH `Honua.Ai` and `Honua.Azure` may reference, and it already has
  `InternalsVisibleTo` for both. Adds `Microsoft.Extensions.AI.Abstractions` (cloud-neutral, allowed).
- **`Honua.Azure`** — `AzureOpenAiChatClientAdapter` (`IChatClient` over `Azure.AI.OpenAI`) +
  `AzureOpenAiChatClientFactory : IAzureOpenAiChatClientFactory` + `AddAzureOpenAiChatClient` DI ext.
  Adds `Azure.AI.OpenAI` + `Microsoft.Extensions.AI`.
- **`Honua.Ai`** — `AzureOpenAiWorkflowGenerationProvider : IWorkflowGenerationProvider`
  (mirrors `BedrockWorkflowGenerationProvider`, consumes the Hosting factory interface, reuses the
  Ai-internal schema/prompt/mapper). Registered (self-gating via `IsConfigured`) in
  `AddWorkflowGeneration`. Adds `Microsoft.Extensions.AI` to `Honua.Ai`.
- **`Honua.Server`** — binds `IAzureOpenAiChatClientFactory` -> Azure impl (conditional on
  `HonuaIncludeAzure`). If Azure isn't compiled in, the provider's `IsConfigured` returns false and
  it is simply unselectable (no factory bound) — fail-safe.

This is a CLEANER split than #1737's Bedrock (which leaked AWS SDK into Honua.Ai). The AWS side
could later be refactored to the same shape (factory iface in Hosting, adapter in Honua.Aws).

### #1745 Azure Key Vault license resolver
- **`Honua.Hosting/Features/Licensing/`** (namespace `Honua.Infrastructure.Licensing`) —
  `ILicenseContentSecretResolver` defined as a PROVISIONAL draft (commented: pending #1742 which
  owns the real interface). Plus `LicenseContentSecretRef` on `LicenseOptions`. Wired into
  `FileBackedLicenseService.LoadConfiguredLicenseAsync`: resolve the ref at startup, treat the
  resolved value as the inline envelope. FAIL-SAFE: any resolver error -> Community, never crash.
- **`Honua.Azure`** — `AzureKeyVaultLicenseContentResolver : ILicenseContentSecretResolver`
  resolving `azure:keyvault:https://<vault>.vault.azure.net/<secret>` via managed identity, reusing
  the proven HTTP+IMDS-token approach from
  `Honua.Postgres/.../ConnectionSecretResolvers/AzureKeyVaultResolver.cs` (light deps, no Azure SDK
  needed for the call itself — but it lives in Honua.Azure to keep the licensing seam cloud-neutral).
  This is a SEPARATE resolver from `IConnectionSecretResolver` — do not conflate.

## Canonical config (MUST match iac #59)
```
WorkflowGeneration:DefaultProvider = azureopenai
WorkflowGeneration:Providers:azureopenai:Endpoint        (https://<resource>.openai.azure.com)
WorkflowGeneration:Providers:azureopenai:Model           (= Azure deployment name)
WorkflowGeneration:Providers:azureopenai:ApiVersion
WorkflowGeneration:Providers:azureopenai:MaxTokens
WorkflowGeneration:Providers:azureopenai:TimeoutSeconds
```
Auth: Entra Managed Identity preferred (`DefaultAzureCredential`; `AZURE_CLIENT_ID` selects the
user-assigned identity). Optional key fallback env: `HONUA_WORKFLOWGEN_AZUREOPENAI_API_KEY`
(reuses the existing `HONUA_WORKFLOWGEN_<ID>_API_KEY` PostConfigure fallback).

```
Licensing:LicenseContentSecretRef = azure:keyvault:https://<vault>.vault.azure.net/pro-license
```

## Provisional / collision items
- `ILicenseContentSecretResolver` is owned by #1742 (AWS, no PR). Defined here PROVISIONALLY; when
  #1742 lands, reconcile the interface (signature/namespace) — likely delete this draft copy.
- `ApiVersion` field is NEW on `WorkflowGenerationProviderOptions` (Bedrock added `Region`; Azure
  OpenAI needs `ApiVersion`). Will collide with #1737's edit of the same Core config file.
- `WorkflowGenerationConfiguration` gains `AzureOpenAiProviderId="azureopenai"` + validator branch
  next to #1737's `bedrock` branch — same-file collision with #1737.
