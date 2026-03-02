#!/usr/bin/env node
import { fileURLToPath } from "node:url";
import { McpServer, ResourceTemplate } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { HonuaClient } from "@honua/sdk-js";
import type { HonuaTransport } from "@honua/sdk-js";

import * as listServices from "./tools/list-services.js";
import * as describeLayer from "./tools/describe-layer.js";
import * as queryFeatures from "./tools/query-features.js";
import * as countFeatures from "./tools/count-features.js";
import * as getExtent from "./tools/get-extent.js";
import * as statistics from "./tools/statistics.js";
import * as servicesResource from "./resources/services.js";
import * as layerSchemaResource from "./resources/layer-schema.js";

export interface RuntimeOptions {
  baseUrl: string;
  apiKey: string | undefined;
  transport: HonuaTransport;
}

export function resolveRuntimeOptions(env: NodeJS.ProcessEnv): RuntimeOptions {
  const baseUrl = env.HONUA_BASE_URL;
  if (!baseUrl) {
    throw new Error("HONUA_BASE_URL environment variable is required.");
  }

  let parsedUrl: URL;
  try {
    parsedUrl = new URL(baseUrl);
  } catch {
    throw new Error(`HONUA_BASE_URL must be a valid absolute URL: ${baseUrl}`);
  }

  if (parsedUrl.protocol !== "http:" && parsedUrl.protocol !== "https:") {
    throw new Error(`HONUA_BASE_URL must use http or https: ${baseUrl}`);
  }

  const rawTransportInput = env.HONUA_TRANSPORT ?? "grpc-web";
  const normalizedTransport = rawTransportInput.trim().toLowerCase();
  const transport: HonuaTransport | undefined =
    normalizedTransport === "grpc-web" || normalizedTransport === "grpc" || normalizedTransport === "grcp"
      ? "grpc-web"
      : normalizedTransport === "rest"
        ? "rest"
        : undefined;
  if (!transport) {
    throw new Error(
      `HONUA_TRANSPORT must be "grpc-web" (aliases: "grpc", "grcp") or "rest", received "${rawTransportInput}"`,
    );
  }

  return {
    baseUrl,
    apiKey: env.HONUA_API_KEY,
    transport,
  };
}

export function createClientFromEnv(env: NodeJS.ProcessEnv = process.env): HonuaClient {
  const options = resolveRuntimeOptions(env);
  return new HonuaClient({
    baseUrl: options.baseUrl,
    apiKey: options.apiKey,
    transport: options.transport,
  });
}

export function createServer(client: HonuaClient) {
  const server = new McpServer({
    name: "honua",
    version: "0.0.1-alpha.0",
  });

  // ── Tools ──────────────────────────────────────────────────────

  server.tool(
    "honua_list_services",
    "Discover all available feature services. Set includeDetails=true for descriptions, layer counts, and spatial references.",
    listServices.schema.shape,
    async (args) => listServices.execute(client, listServices.schema.parse(args)),
  );

  server.tool(
    "honua_describe_layer",
    "Get full schema for a layer — fields, geometry type, extent, relationships.",
    describeLayer.schema.shape,
    async (args) => describeLayer.execute(client, describeLayer.schema.parse(args)),
  );

  server.tool(
    "honua_query_features",
    "Query features with attribute filters, spatial filters, field selection, and pagination. returnGeometry defaults to false to save tokens.",
    queryFeatures.schema.shape,
    async (args) => queryFeatures.execute(client, queryFeatures.schema.parse(args)),
  );

  server.tool(
    "honua_count_features",
    "Count features matching a filter without returning data. Use before querying to check cardinality.",
    countFeatures.schema.shape,
    async (args) => countFeatures.execute(client, countFeatures.schema.parse(args)),
  );

  server.tool(
    "honua_get_extent",
    "Get the spatial bounding box of features matching a filter.",
    getExtent.schema.shape,
    async (args) => getExtent.execute(client, getExtent.schema.parse(args)),
  );

  server.tool(
    "honua_statistics",
    "Compute aggregate statistics (count, sum, avg, min, max, stddev) on a field, optionally grouped.",
    statistics.schema.shape,
    async (args) => statistics.execute(client, statistics.schema.parse(args)),
  );

  // ── Resources ──────────────────────────────────────────────────

  server.resource("services-catalog", servicesResource.uri, async (uri) => servicesResource.read(client));

  server.resource(
    "layer-schema",
    new ResourceTemplate(layerSchemaResource.uriTemplate, { list: undefined }),
    async (uri, params) =>
      layerSchemaResource.read(client, params.encodedServiceId as string, params.layerId as string),
  );

  return server;
}

async function main() {
  const client = createClientFromEnv();

  const server = createServer(client);
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main().catch((err) => {
    process.stderr.write(`Fatal: ${err instanceof Error ? err.message : String(err)}\n`);
    process.exit(1);
  });
}
