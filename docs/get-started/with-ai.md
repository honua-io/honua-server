---
type: guide
title: "Get started with AI"
description: "Start with an agent and local Docker, verify the first dataset, then create and publish maps and apps, operate Honua, and move to cloud when ready."
tags: [getting-started, agents, deployment, publishing, studio]
aliases: ["setup Honua with AI", "install with an agent", "Docker or cloud", "Lambda ECS EKS Batch Redis databases", "secrets from a prompt", "AI GIS getting started"]
relations:
  - predicate: uses
    target: /guides/workflows/use-workflow-language.md
  - predicate: starts_with
    target: /guides/workflows/install-configure.md
  - predicate: continues_with
    target: /guides/workflows/connect-publish-data.md
  - predicate: continues_with
    target: /guides/operate/README.md
  - predicate: continues_with
    target: /guides/workflows/design-studio.md
---
# Get started with AI

**Set up Honua with AI. Add services and layers and operate Honua with AI.
Make maps and apps in realtime with AI.**

Start in an agent that can use your authorized local or cloud tools. Describe
the target and the result you want. After Honua starts, connect the agent to
its MCP endpoint for data administration, operations, and map composition.
Honua Studio provides the conversational browser experience for realtime
map and app creation.

This guide follows one deployment from installation to a working application.
Prompts are examples of intent, not exact phrases or a new command language.
The agent uses the linked procedures and the installed tool schemas, reuses
choices already made, and asks only about missing decisions that affect the result.

## 1. Ask your agent to set up Honua in Docker

> Set up Honua in Docker on this machine.

Start locally to evaluate the platform before choosing cloud infrastructure.
The agent should inspect the machine first, reuse your existing choices, and
ask only about consequential decisions it cannot discover. A useful follow-up
is “Use the sample dataset, or start with your own data?” If no installation
location is established, it can propose a dedicated directory and explain
that the configuration and data will persist there and in named volumes.
Docker already establishes the target; there is no need to ask for an AWS
account, deployment topology, or Git repository to begin.

### Agent prerequisite check

Before creating the installation, use the host's tools to check:

| Check | What the agent must establish |
| --- | --- |
| Host and Docker context | OS/architecture and whether the selected Docker context is actually local. Do not install on a remote daemon by accident. |
| Docker client and daemon | `docker --version` establishes the client; `docker info` establishes daemon access. Distinguish missing Docker, an unreachable daemon, and permission denial. |
| Compose | `docker compose version` meets the [quickstart](quickstart.md) minimum, currently 2.23.1. A standalone old `docker-compose` command is not the same prerequisite. |
| Supporting tools | Python 3.11+, OpenSSL 3, and the shell utilities used by the selected procedure. On Windows, choose the documented PowerShell/WSL path. |
| Capacity and ownership | Available disk space on the installation and Docker storage filesystems, Docker memory allocation, a writable installation directory, and existing projects that must be preserved. Report measured values; do not invent a certified resource minimum. |
| Ports | Unused loopback HTTP and gRPC ports, propagated consistently into configuration and client URLs. |
| Published artifacts | The chosen server image is anonymously pullable for the intended platform, and the exact client versions can be fetched. Record the resolved image digest. |

A missing prerequisite needs a specific diagnosis and the supported installation
or startup procedure for that host. Installing Docker or changing host permissions
must respect the user's authority; do not silently change global Docker settings
or stop unrelated containers. Once prerequisites work, resume this same journey.
A registry “not found” is an artifact availability failure, not proof that Docker
is broken or that the evaluator needs registry credentials.

### Start, connect, and prove the installation

Follow the [package quickstart](quickstart.md). Its `HONUA_IMAGE` is the
2026.1 release channel `ghcr.io/honua-io/honua-server:2026.1-rc`. That tag is
not published until release promotion. The
[artifact receipt](../reference/local-preview-artifact.md) qualifies
`nightly-0e03bdd` only; do not treat its digest or checks as evidence for the
RC tag. Until the channel tag exists, a local evaluation uses that preview
image and says so. Create an isolated Compose
project, generate installation secrets directly into protected local files,
start Honua/PostGIS/Redis with persistent volumes, and verify readiness and an
authenticated operation. Then connect the agent as described in step 3. For a
first evaluation, publish the quickstart's small sample table and query its
expected identities and coordinates. Reuse that layer for the map steps below.

Verify persistence by restarting this project's services and querying the same
saved resources without recreating them. Report the install directory, project,
image digest, endpoint, verified actions, and stop/resume commands. Keep secret
values out of the conversation.

**Done when:** the intended server is ready, authenticated access works, the
sample layer can be queried correctly, and its state survives restart. MCP
connection and a rendered map have their own checks; container startup proves
neither. If an artifact or operation is unavailable, stop that dependent path
and report the failed check instead of claiming the evaluation succeeded.

For a durable single-host deployment, continue to
[production Compose](../guides/deploy/docker-compose.md). Cloud is a later choice
in step 10, after the local evaluation demonstrates the behavior you need.

## 2. Use secrets from a prompt

Name the secret and where it should be used; keep its value in the credential
mechanism. For example:

> Register our planning database using
> `aws:secretsmanager:honua/connections/planning` for its credentials. Use the
> existing runtime identity, permit that reference for this connection, and test
> connectivity. Report the reference and test result without displaying the value.

Or, for a local secured source import:

> Use `env:HONUA_IMPORT_ARCGIS_TOKEN` for this ArcGIS import. The variable is
> already injected into the server environment. Allow that exact reference and
> test access without printing the token.

The agent needs to distinguish three steps:

1. **Provision or locate the value.** Use the authorized secret-manager or host
   tooling. Generate new installation secrets directly into the protected store
   or installation file. If an external credential must be supplied, use the
   host's secure entry mechanism instead of pasting it into chat.
2. **Grant runtime access.** The server or worker identity must be able to read
   the secret. A name in a prompt does not make it available inside a container,
   Lambda function, or Batch worker.
3. **Bind the reference.** Use the selected API's actual fields and configure
   its request-reference allowlist when required. Test from the runtime that
   will consume it, retaining only a redacted result.

The formats are specific to the consuming surface:

| Use | Supported binding to inspect |
| --- | --- |
| Platform database connection | `ConnectionStrings__DefaultConnection` can contain a supported provider-prefixed reference. |
| Registered PostGIS connection | `secretReference` with `secretType`, as defined by the connection API; do not also send `password`. |
| Queued secured service import | `credentials.accessTokenSecretReference` or `passwordSecretReference`; queued imports reject inline token/password values. |
| Lambda configuration document | `HONUA_SETTINGS_DOCUMENT=aws:secretsmanager:<name-or-arn>` points to the settings document and avoids filling the function environment with settings. |

Request-supplied secret references are refused unless permitted by
`Security__RequestSecretReferences__*`. Use an exact allowed environment variable
or the intended secret-reference prefix. Startup configuration references and
request-supplied references are different mechanisms. See
[secret references](../guides/deploy/configuration.md#secret-references) and
[Lambda settings documents](../guides/deploy/cloud-deployments.md#keep-settings-out-of-the-4-kb-lambda-environment).

**Done when:** the actual consumer resolves the reference and its connection or
import authentication succeeds. Conversation, reports, generated app assets,
and ordinary logs contain no secret value.

## 3. Connect the agent to Honua

> Connect to this Honua deployment. Discover the available setup, data, operations,
> and Studio tools under my current identity. Tell me which parts of our intended
> workflow this deployment can run.

Follow [Connect AI agents](../guides/connect/ai-agents-mcp.md). The server does
not need an existing published layer to connect or configure its first service.
Discover capabilities and the tools' current input/output schemas. Use supported
MCP or SDK surfaces for the work below; when a wrapper is missing, use the
documented generated client or deployment tool, not an invented command.

**Done when:** the agent can inspect the intended environment under the right
identity and distinguish unavailable capabilities from permission failures.

## 4. Bring in services, files, or database tables

Choose a path based on where the data already lives:

| Starting point | Example request | Procedure and result |
| --- | --- | --- |
| ArcGIS service | “Import these shelter layers into a new Honua dataset; preserve the original service and report anything that does not transfer.” | [Service import](../guides/publish/import-from-arcgis-services.md). Discover the service root, select layers, import, and inspect fidelity and job completion. |
| GeoServer | “Assess and bring these GeoServer layers into Honua.” | [GeoServer migration](../guides/migrate/from-geoserver.md), with supported-source and operation limits checked first. |
| Local or uploaded file | “Import this GeoPackage as a new dataset, then publish it to the emergency service.” | [File import](../guides/publish/import-files.md). Inspect schema/CRS, select a fresh destination, follow any job, then publish. |
| Existing database | “Connect our planning PostGIS database and expose its parcels table without copying it.” | [Existing databases](../guides/publish/serve-existing-databases.md). Register/test the connection, discover tables, and select source-backed publication. |
| An intended managed copy | “Create a separate managed copy of this table for the application.” | [Managed publication](../reference/admin-api/connections-and-layers.md#publish-an-editable-managed-copy). Materialize the selected data and verify completion; it does not remain synchronized with the source table. |

The platform database and connected source databases need not be the same.
PostGIS, SQL Server, Oracle, DuckDB, and MySQL/MariaDB have different provider
capabilities and registration paths; inspect the
[provider matrix](../reference/configuration/data-sources/README.md).

Keep **import**, **connect**, and **publish** distinct. Import moves data into a
destination. A connection makes a source accessible. Publication exposes the
selected resource and storage mode through a service. Source-backed and managed
publication have different copying, editing, and refresh behavior.

**Done when:** the intended data is available, schema and spatial meaning are
checked, any job is terminal, and migration omissions or source changes are
reported. A job ID or matching row count alone is insufficient.

## 5. Deploy the runtime, then publish GIS services

“Deploy a service” can refer to different objects. The agent should use the
current context and distinguish them:

| Requested result | Meaning |
| --- | --- |
| Deploy Honua serving or a worker | Run a specific artifact with its configuration and dependencies. |
| Publish a GIS service/layer | Register the intended resource, service membership, protocols, and access. |
| Publish a map/app | Activate a saved composition for its intended audience. |

> Publish the shelter and flood-zone layers in the emergency service. Enable the
> interfaces our GIS clients and map need, preserve team-only access, and verify
> a query as a team member.

Use [Publish layers](../guides/publish/publish-layers.md) and the
[service settings contract](../reference/admin-api/connections-and-layers.md#service-and-layer-settings).
Use the returned service and layer identities. Check the effective capabilities,
protocol enablement, metadata, and access through the intended client interface.
Registering a connection or adding a drawing layer to a map does not complete
this publication workflow.

**Done when:** the intended client can discover and query the service, the
correct layers are present, and access matches the requested audience.

## 6. Operate and update with verified rollback

For everyday operations:

> Check this deployment's health and active jobs. Explain any findings, perform
> the supported actions within my existing authority, and verify the outcome.

For a version update:

> Prepare and apply the selected Honua release to this environment using its
> protected update path. Check database compatibility, backups, active jobs,
> and rollback eligibility. Follow required policy decisions and verify serving
> and job continuity afterward. If recovery is triggered, verify the restored
> version and behavior before reporting success.

Follow [Operating Honua](../guides/operate/README.md) and
[agent-driven updates](../guides/deploy/upgrade-and-rollback.md#agent-driven-upgrade-over-mcp).
The agent discovers the actual deploy backend, release preflight, operation
identity, observation window, and recovery mechanism. Direct configuration
operations and protected deployment proposals have different policies; a
proposal stays pending until its required separate decision occurs.

An image rollback does not reverse a database migration. The old reader must
recognize the resulting schema; otherwise use the documented recovery plan.
During Lambda/Batch updates, retain the original durable job and provider
identity and observe that execution instead of submitting it again.
For Compose, use its [upgrade and rollback procedure](../guides/deploy/docker-compose.md#upgrade--rollback).

**Done when:** the intended artifact and configuration are active, the declared
health checks and representative GIS operations succeed, and in-flight jobs
have the expected outcomes. A rollback is complete only after the restored
version and its behavior are verified. State unavailable protection or
unresolved recovery plainly.

## 7. Make and style maps and apps in realtime

> Make an emergency-planning app with our shelters and flood zones. Put a layer
> list beside the map, show shelter capacity in a table, and style shelters by
> capacity. Make the flood-zone fill quieter so the shelters stand out.

Then refine it naturally:

> Zoom to the selected shelter. Hide the flood layer for a moment. Undo that
> last style change. Add a legend and save this version.

Use [Studio](../studio/README.md) for the current browser or embedded Preview,
or drive the same draft tools from an agent. The working loop is **ask → see
the map/app change → refine → undo/redo**. It uses typed composition tools,
current draft generations, and the real renderer. It does not route every
creative request through the old Console builders.

Style the selected drawing layer when the request concerns one map. Changing a
shared service style affects other consumers and is a separate scope; see
[Style maps](../guides/style/style-maps.md). Use the fields and renderer features
actually supported by the layer and installed client. Browser Studio is Preview;
check its current vector/raster/3D boundaries rather than assuming all server
formats render in every client.

Save a version when requested and verify that it reopens. Publishing or sharing
that version is a separate action: preserve the requested audience and check
data dependencies. The current server has direct admin publication and non-admin
proposal outcomes; browser support is narrower. See the
[publication workflow](../guides/workflows/publish-share-map.md) and
[current composition-policy limitations](../studio/mcp-tools.md#composition-policy).

**Done when:** the current renderer shows the requested composition and style,
undo/redo preserves unrelated work, and any requested saved version can be
reopened. A preview plan is not a rendered result; a preview is not automatically
a saved or shared app.

## 8. Publish the map or app back to Honua

> Publish this saved map to Honua for the audience we already chose. Verify that
> the published version opens and its data is accessible to that audience.

Follow [publish and share a saved map](../guides/workflows/publish-share-map.md).
Bind the saved item, current version, and content hash. Inspect the actual
publication response: an authorized admin can receive `Published` and a share
URL directly; other callers can receive `AwaitingApproval`. A pending proposal
is not a published map. The current browser adapter has narrower support than
the server; see [Studio publication limits](../studio/mcp-tools.md#composition-policy).

**Done when:** the intended audience can reopen the exact published composition
and read its permitted dependencies. A saved draft, generated URL, or successful
publication request alone does not establish that outcome.

## 9. Optionally use Git for Studio AI workflows

> Keep this app's source assets in our Git workspace. Preserve unrelated changes
> and show the scoped changes made by each design turn.

Git is optional; the map/app journey does not require a repository or a pull
request for each creative edit. The current Studio implementation provides
composition history synchronized with server drafts. The
[Git workspace adapter](../concepts/domain/studio-workspace.md) is a design
contract, not a claim of a shipped Studio Git integration. Use an agent host's
existing Git tools only when the workflow actually has source assets and an
authorized worktree. Bind the selected files and base state, preview the result,
and undo scoped changes without resetting unrelated work.

**Done when:** the chosen host can show and recover the actual source changes,
and the corresponding map/app preview is verified. A Git commit does not publish
a map to Honua; perform step 8 when publication is requested.

## 10. Move to cloud when ready

For cloud:

> Set up Honua in my authorized AWS account and region. Use Lambda for serving,
> PostGIS for platform data, Redis for durable work, S3 for shared files, and
> Batch for large imports and processing. Show the selected deployment and
> resource scope before provisioning, then verify the complete path.

Replace Lambda with ECS or EKS when that is the intended hosting model. The
agent follows [cloud deployment](../guides/deploy/cloud-deployments.md) and the
versioned [infrastructure operator guide](https://github.com/honua-io/honua-iac/blob/trunk/docs/operator-deployment.md).
An existing cluster uses the [Kubernetes guide](../guides/deploy/kubernetes.md).
Use the correct immutable artifact for the selected runtime; a generic web
image and a Lambda image are different deployment inputs.

| Component | Role in this deployment |
| --- | --- |
| Docker Compose | A local or single-host installation. |
| ECS/Fargate | Long-lived serving containers behind a load balancer. |
| EKS | Serving on an existing or provisioned Kubernetes cluster through Helm. |
| Lambda | Request-serving instances with a target-specific image; background work must survive outside the request. |
| AWS Batch | Separate execution for supported long-running work; it is not the map/API endpoint. |
| PostgreSQL/PostGIS | The platform's operational database and managed spatial storage. |
| Redis | Shared state for supported durable jobs, proposals, scheduling, and coordination; a serving-only topology can omit it with explicit capability limits. |
| S3 or equivalent shared storage | Files and artifacts that separate API and worker processes must both reach. |
| Existing GIS databases | Additional data sources connected for publication; these are distinct from the platform database. |

For Lambda, a large file import requires a compatible durable Batch runner and
shared file storage. Large managed-layer publication also needs a compatible
remote runner, but reads its source database directly. Configure and verify the
API and worker together; adding Redis alone does not create a Batch executor.
See [Batch execution](../operator/geoprocessing-aws-batch.md) and
[import runners](../guides/publish/import-files.md#where-background-imports-run).

Cloud deployment options and agent provisioning support are different facts.
The current `honua-devops` provisioning executor supports the `aws-ecs` stack;
its `install_handoff` and `verify_install_handoff` tools distinguish writing
connection settings from proving authenticated MCP access. Lambda and EKS
infrastructure guides do not imply that this executor implements those targets.
Use the selected target's actual documented deployment tools and report any
missing automation. Day-2 health, findings, and operational proposals belong to
the installed server's MCP surface.

**Done when:** the chosen cloud artifact, identity, persistence, and workers are
verified, followed by the same representative data and map outcomes demonstrated
locally. An ECS receipt does not qualify Lambda, EKS, or Docker.

## Continue with the shared workflow language

The [GIS workflow-language guide](../guides/workflows/use-workflow-language.md)
explains the meanings behind these journeys. Its OKF concepts connect user
intent, typed objects, supported interfaces, and observable outcomes. They are
the agent's reference; people do not need to write workflow YAML to use Honua.
