# Validation Runner

`Honua.Validation.Runner` is the first slice of the validation refactor. It is
intentionally additive: it does not replace the current Terraform or shell
validation flow yet.

The goal is to define a stable automation surface that works for:

- platform engineers
- customers running Honua in their own environments
- AI DevOps agents

## Why It Exists

The current validation system still depends on:

- large shell scripts
- many implicit environment variables
- target-specific behavior spread across workflow YAML, shell, and test code

This runner starts moving the contract into a typed, machine-readable form.

## Current Scope

The first slice provides:

- a target catalog for:
  - `aws-ecs`
  - `aws-lambda`
  - `aws-eks`
  - `azure-aca`
  - `azure-functions`
  - `azure-aks`
- explicit target capabilities
- explicit required Terraform outputs
- explicit required environment variables
- machine-readable request validation
- structured issue codes and phase metadata for automation

It does **not** run Terraform or replace the existing post-apply shell yet.

## CLI

Describe the supported targets:

```bash
dotnet run --project src/Honua.Validation.Runner -- describe-targets
```

Print an example request for a target:

```bash
dotnet run --project src/Honua.Validation.Runner -- print-example-request --target azure-functions
```

Validate a request file:

```bash
dotnet run --project src/Honua.Validation.Runner -- validate-request --input request.json
```

The validation result now includes:

- `status`
- `phase`
- `issues[]`
- `issues[].code`
- `issues[].field`
- `issues[].suggestedAction`

That shape is intended for:

- customer CI systems
- AI DevOps agents
- human triage without log scraping

## Design Direction

The next slices should move:

1. capability resolution out of shell and into the runner
2. post-apply orchestration into typed C# entry points
3. structured result artifacts into JSON instead of log scraping
4. customer and AI-agent configuration onto one documented schema
