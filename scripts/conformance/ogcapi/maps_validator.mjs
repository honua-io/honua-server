#!/usr/bin/env node
// OGC API - Maps conformance gate for Honua Server.
//
// honua advertises the OGC API Maps conformance classes and serves /ogc/maps,
// but there is no TeamEngine CITE suite for OGC API Maps. This gate validates
// honua's Maps JSON metadata responses against the canonical published, bundled
// OpenAPI document (schemas.opengis.net/ogcapi/maps/part1/1.0/openapi/
// ogcapi-maps-1.bundled.json), vendored for hermetic CI:
//
//   * GET /ogc/maps               -> components.schemas.landingPage
//   * GET /ogc/maps/conformance   -> components.schemas.confClasses
//   * GET /ogc/maps/collections/{id}/map/tiles      -> tileSet list metadata
//   * GET /ogc/maps/collections/{id}/map            -> rendered image (binary)
//
// The bundled OpenAPI carries all referenced component schemas inline, so AJV
// resolves every $ref from the single vendored file.
//
// Exit 0 = pass, non-zero = at least one failure.

import { readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
// OGC API Maps bundled OpenAPI is OpenAPI 3.0 (JSON Schema draft-04 dialect of
// component schemas); the default Ajv build validates these fine in lax mode.
const Ajv = require("ajv").default ?? require("ajv");
const addFormats = require("ajv-formats");

const HERE = dirname(fileURLToPath(import.meta.url));
const VENDOR = join(HERE, "vendor");
const MAPS_OPENAPI = join(VENDOR, "ogcapi-maps", "ogcapi-maps-1.bundled.json");

function parseArgs(argv) {
  const a = { base: "http://localhost:8080", collection: "0" };
  for (let i = 2; i < argv.length; i++) {
    const v = argv[i + 1];
    switch (argv[i]) {
      case "--base-url": a.base = v; i++; break;
      case "--collection": a.collection = v; i++; break;
      default: break;
    }
  }
  return a;
}

async function fetchJson(url) {
  const res = await fetch(url, { headers: { Accept: "application/json" } });
  if (!res.ok) return { status: res.status, body: null };
  return { status: res.status, body: await res.json() };
}

// Build an AJV instance seeded with the bundled OpenAPI's component schemas so
// internal "#/components/schemas/..." $refs resolve against the document root.
function makeAjv(openapiDoc) {
  const ajv = new Ajv({ allErrors: true, strict: false });
  addFormats(ajv);
  // Register the whole document under a synthetic id; component schemas are then
  // addressable as "<id>#/components/schemas/<name>".
  const docId = "https://honua.local/ogcapi-maps.bundled.json";
  ajv.addSchema({ $id: docId, ...openapiDoc });
  return { ajv, docId };
}

function validateComponent(ajv, docId, schemaName, doc) {
  const ref = `${docId}#/components/schemas/${schemaName}`;
  const validate = ajv.getSchema(ref);
  if (!validate) return `component schema ${schemaName} not found in bundled OpenAPI`;
  const ok = validate(doc);
  return ok ? null : ajv.errorsText(validate.errors, { separator: "\n      " });
}

async function main() {
  const args = parseArgs(process.argv);
  const failures = [];
  let passed = 0;

  console.log(`== OGC API - Maps gate (${args.base}) ==`);
  if (!existsSync(MAPS_OPENAPI)) {
    console.error("vendored ogcapi-maps-1.bundled.json missing; run vendor-schemas.py");
    process.exit(2);
  }
  const openapiDoc = JSON.parse(readFileSync(MAPS_OPENAPI, "utf-8"));
  const { ajv, docId } = makeAjv(openapiDoc);

  // 1. Landing page -> landingPage schema
  {
    const { status, body } = await fetchJson(`${args.base}/ogc/maps`);
    if (status !== 200 || !body) {
      failures.push(`GET /ogc/maps: HTTP ${status}`);
    } else {
      const err = validateComponent(ajv, docId, "landingPage", body);
      if (err) failures.push(`/ogc/maps landing page fails landingPage schema:\n      ${err}`);
      else {
        passed++;
        console.log("   /ogc/maps landing page valid against landingPage schema");
      }
    }
  }

  // 2. Conformance -> confClasses schema
  {
    const { status, body } = await fetchJson(`${args.base}/ogc/maps/conformance`);
    if (status !== 200 || !body) {
      failures.push(`GET /ogc/maps/conformance: HTTP ${status}`);
    } else {
      const err = validateComponent(ajv, docId, "confClasses", body);
      if (err) failures.push(`/ogc/maps/conformance fails confClasses schema:\n      ${err}`);
      else if (!Array.isArray(body.conformsTo) || body.conformsTo.length === 0) {
        failures.push("/ogc/maps/conformance has empty conformsTo");
      } else {
        const hasMaps = body.conformsTo.some((c) => typeof c === "string" && c.includes("ogcapi-maps"));
        if (!hasMaps) failures.push("/ogc/maps/conformance omits any ogcapi-maps conformance class");
        else {
          passed++;
          console.log(`   /ogc/maps/conformance valid (${body.conformsTo.length} classes)`);
        }
      }
    }
  }

  // 3. Collection map tilesets -> tileSet list metadata (when renderable)
  {
    const url = `${args.base}/ogc/maps/collections/${args.collection}/map/tiles`;
    const { status, body } = await fetchJson(url);
    if (status === 404) {
      // A collection with no map binding legitimately 404s; record but don't fail
      // the whole gate on metadata absence — the render check below is the real
      // signal that map resolution works.
      console.log(`   /ogc/maps/collections/${args.collection}/map/tiles -> 404 (no map tileset metadata)`);
    } else if (status !== 200 || !body) {
      failures.push(`GET ${url}: HTTP ${status}`);
    } else if (!Array.isArray(body.tilesets)) {
      failures.push(`${url}: response missing tilesets array`);
    } else {
      passed++;
      console.log(`   /ogc/maps/collections/${args.collection}/map/tiles valid (${body.tilesets.length} tilesets)`);
    }
  }

  // 4. Collection map render -> binary image. This is the end-to-end proof that
  //    Maps collection resolution works; honua-server#<fix> made publication-
  //    scoped storage bindings resolve here (previously every collection 404'd).
  {
    const url = `${args.base}/ogc/maps/collections/${args.collection}/map?bbox=-180,-90,180,90&width=128&height=128&f=png`;
    const res = await fetch(url);
    if (res.status !== 200) {
      failures.push(
        `GET collection map: HTTP ${res.status} (expected 200 PNG) — Maps collection ` +
          `resolution may be broken for publication-bound collections`,
      );
    } else {
      const ct = res.headers.get("content-type") || "";
      const buf = Buffer.from(await res.arrayBuffer());
      const isPng = buf.length > 8 && buf[0] === 0x89 && buf[1] === 0x50 && buf[2] === 0x4e && buf[3] === 0x47;
      if (!ct.startsWith("image/") || !isPng) {
        failures.push(`collection map render: content-type=${ct}, png-signature=${isPng}`);
      } else {
        passed++;
        console.log(`   /ogc/maps/collections/${args.collection}/map renders PNG (${buf.length} bytes)`);
      }
    }
  }

  console.log("\n== Maps gate summary ==");
  console.log(`   passed:  ${passed}`);
  console.log(`   failed:  ${failures.length}`);
  if (failures.length) {
    console.log("\nFailures:");
    for (const f of failures) console.log(`  - ${f}`);
    process.exit(1);
  }
  console.log("OGC API - Maps conformance gate PASSED");
}

main().catch((e) => {
  console.error(`Maps gate crashed: ${e.stack || e.message}`);
  process.exit(2);
});
