// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

// Honua.Jobs hosts the durable job-execution substrate originally carved from
// Honua.Server (an Sdk.Web project that supplies Microsoft.Extensions.Logging /
// Hosting as implicit usings). Honua.Jobs targets Sdk so we re-establish the
// same baseline here, letting the moved source files keep their using clauses
// unchanged. Scope is intentionally narrow — only the namespaces the substrate
// already depended on transitively.
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.DependencyInjection.Extensions;
global using Microsoft.Extensions.Options;
