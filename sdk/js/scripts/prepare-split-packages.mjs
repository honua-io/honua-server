#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = path.resolve(SCRIPT_DIR, "..");
const DIST_ROOT = path.join(PROJECT_ROOT, "dist");
const DIST_SRC_ROOT = path.join(DIST_ROOT, "src");
const OUTPUT_ROOT = path.join(DIST_ROOT, "packages");

const rootPackageJsonPath = path.join(PROJECT_ROOT, "package.json");
const rootPackageJson = JSON.parse(fs.readFileSync(rootPackageJsonPath, "utf8"));
const version = rootPackageJson.version;
const engines = rootPackageJson.engines ?? { node: ">=20.0.0" };

if (!fs.existsSync(DIST_SRC_ROOT)) {
  process.stderr.write(
    `Missing build output at ${DIST_SRC_ROOT}. Run "npm run build" before split packaging.\n`,
  );
  process.exit(1);
}

resetOutputRoot();

createSdkPackage();
createCompatPackage();
createMigrationPackage();

process.stdout.write(`splitPackagesWritten=${OUTPUT_ROOT}\n`);

function resetOutputRoot() {
  fs.rmSync(OUTPUT_ROOT, { recursive: true, force: true });
  fs.mkdirSync(OUTPUT_ROOT, { recursive: true });
}

function createSdkPackage() {
  const packageRoot = path.join(OUTPUT_ROOT, "honua-sdk");
  fs.mkdirSync(packageRoot, { recursive: true });

  copyDirectory(path.join(DIST_SRC_ROOT, "core"), path.join(packageRoot, "core"));
  copyDirectory(path.join(DIST_SRC_ROOT, "esri-compat"), path.join(packageRoot, "esri-compat"));
  copyDirectory(path.join(DIST_SRC_ROOT, "expr"), path.join(packageRoot, "expr"));
  copyDirectory(path.join(DIST_SRC_ROOT, "gen"), path.join(packageRoot, "gen"));
  copyDirectory(path.join(DIST_SRC_ROOT, "interactions"), path.join(packageRoot, "interactions"));
  copyDirectory(path.join(DIST_SRC_ROOT, "map"), path.join(packageRoot, "map"));
  copyDirectory(path.join(DIST_SRC_ROOT, "style"), path.join(packageRoot, "style"));
  copyFile(path.join(DIST_SRC_ROOT, "honua.js"), path.join(packageRoot, "index.js"));
  copyFile(path.join(DIST_SRC_ROOT, "honua.d.ts"), path.join(packageRoot, "index.d.ts"));

  writePackageJson(packageRoot, {
    name: "@honua/sdk",
    description: "Honua JavaScript SDK core client",
    main: "./index.js",
    types: "./index.d.ts",
    exports: {
      ".": {
        types: "./index.d.ts",
        default: "./index.js",
      },
    },
    dependencies: {
      "@bufbuild/protobuf": rootPackageJson.dependencies["@bufbuild/protobuf"],
      "@connectrpc/connect": rootPackageJson.dependencies["@connectrpc/connect"],
      "@connectrpc/connect-web": rootPackageJson.dependencies["@connectrpc/connect-web"],
    },
  });

  writeReadme(
    packageRoot,
    [
      "# @honua/sdk",
      "",
      "Core Honua JavaScript SDK client APIs.",
      "",
      "This package is generated from `@honua/sdk-js` build artifacts.",
    ].join("\n"),
  );
}

function createCompatPackage() {
  const packageRoot = path.join(OUTPUT_ROOT, "honua-sdk-esri-compat");
  fs.mkdirSync(packageRoot, { recursive: true });

  copyDirectory(path.join(DIST_SRC_ROOT, "core"), path.join(packageRoot, "core"));
  copyDirectory(path.join(DIST_SRC_ROOT, "esri-compat"), path.join(packageRoot, "esri-compat"));
  copyDirectory(path.join(DIST_SRC_ROOT, "gen"), path.join(packageRoot, "gen"));
  copyFile(path.join(DIST_SRC_ROOT, "esri-compat-entry.js"), path.join(packageRoot, "index.js"));
  copyFile(path.join(DIST_SRC_ROOT, "esri-compat-entry.d.ts"), path.join(packageRoot, "index.d.ts"));

  writePackageJson(packageRoot, {
    name: "@honua/sdk-esri-compat",
    description: "Esri compatibility bridge APIs for Honua JavaScript migration",
    main: "./index.js",
    types: "./index.d.ts",
    exports: {
      ".": {
        types: "./index.d.ts",
        default: "./index.js",
      },
    },
    dependencies: {
      "@bufbuild/protobuf": rootPackageJson.dependencies["@bufbuild/protobuf"],
      "@connectrpc/connect": rootPackageJson.dependencies["@connectrpc/connect"],
      "@connectrpc/connect-web": rootPackageJson.dependencies["@connectrpc/connect-web"],
    },
  });

  writeReadme(
    packageRoot,
    [
      "# @honua/sdk-esri-compat",
      "",
      "Compatibility bridge APIs for migrating ArcGIS JavaScript apps to Honua.",
      "",
      "This package is generated from `@honua/sdk-js` build artifacts.",
    ].join("\n"),
  );
}

function createMigrationPackage() {
  const packageRoot = path.join(OUTPUT_ROOT, "honua-migrate");
  fs.mkdirSync(packageRoot, { recursive: true });

  copyDirectory(path.join(DIST_SRC_ROOT, "migration"), path.join(packageRoot, "migration"));
  copyFile(path.join(DIST_SRC_ROOT, "migration-entry.js"), path.join(packageRoot, "index.js"));
  copyFile(path.join(DIST_SRC_ROOT, "migration-entry.d.ts"), path.join(packageRoot, "index.d.ts"));
  fs.chmodSync(path.join(packageRoot, "migration", "cli.js"), 0o755);

  writePackageJson(packageRoot, {
    name: "@honua/honua-migrate",
    description: "ArcGIS-to-Honua migration scanner, codemod, and reporting tools",
    main: "./index.js",
    types: "./index.d.ts",
    bin: {
      "honua-migrate": "./migration/cli.js",
    },
    exports: {
      ".": {
        types: "./index.d.ts",
        default: "./index.js",
      },
      "./cli": {
        default: "./migration/cli.js",
      },
    },
    dependencies: {
      typescript: rootPackageJson.devDependencies.typescript,
    },
  });

  writeReadme(
    packageRoot,
    [
      "# @honua/honua-migrate",
      "",
      "Migration tooling for ArcGIS JavaScript to Honua transitions.",
      "",
      "CLI:",
      "",
      "```bash",
      "npx @honua/honua-migrate scan ./src",
      "npx @honua/honua-migrate codemod ./src --write --report migration-report.json",
      "npx @honua/honua-migrate reconcile --source-base-url https://source.example --source-service-id parcels --target-base-url https://target.example --target-service-id parcels --layer-id 0 --report reconcile-report.json",
      "```",
      "",
      "This package is generated from `@honua/sdk-js` build artifacts.",
    ].join("\n"),
  );
}

function writePackageJson(packageRoot, overrides) {
  const packageJson = {
    name: overrides.name,
    version,
    description: overrides.description,
    type: "module",
    main: overrides.main,
    types: overrides.types,
    exports: overrides.exports,
    bin: overrides.bin,
    dependencies: overrides.dependencies,
    engines,
  };

  fs.writeFileSync(
    path.join(packageRoot, "package.json"),
    `${JSON.stringify(packageJson, null, 2)}\n`,
    "utf8",
  );
}

function writeReadme(packageRoot, contents) {
  fs.writeFileSync(path.join(packageRoot, "README.md"), `${contents}\n`, "utf8");
}

function copyFile(sourcePath, destinationPath) {
  fs.mkdirSync(path.dirname(destinationPath), { recursive: true });
  fs.copyFileSync(sourcePath, destinationPath);
}

function copyDirectory(sourceDirectory, destinationDirectory) {
  fs.cpSync(sourceDirectory, destinationDirectory, { recursive: true });
}
