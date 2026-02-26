import fs from "node:fs";
import path from "node:path";
import ts from "typescript";

const SOURCE_EXTENSIONS = new Set([".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"]);
const SKIP_DIRS = new Set(["node_modules", "dist", ".git"]);
const DEFAULT_COMPAT_IMPORT_PATH = "@honua/sdk-esri-compat";
const ESRI_LEAFLET_IMPORT_PATH = "esri-leaflet";
const ESRI_LEAFLET_NAMESPACE = "HonuaEsriLeaflet";
const TODO_MARKER = "TODO(honua-migrate)";
const CJS_REQUIRE_MANUAL_REASON =
  "CommonJS require constructors are not auto-migrated; convert the module to ESM and rerun.";
const ESRI_LEAFLET_UNSUPPORTED_CONSTRUCTOR_REASON =
  "No deterministic esri-leaflet mapping for this constructor; requires manual migration.";
const ESRI_LEAFLET_UNSUPPORTED_DYNAMIC_IMPORT_REASON =
  "Dynamic import has no deterministic esri-leaflet mapping; requires manual migration.";

export type CodemodTarget = "honua-compat" | "esri-leaflet";

export type CodemodConstructorKind =
  | "feature-layer"
  | "graphics-layer"
  | "group-layer"
  | "map-image-layer"
  | "tile-layer"
  | "map"
  | "map-view"
  | "scene-view"
  | "web-map"
  | "layer-list"
  | "legend-widget"
  | "popup-widget"
  | "home-widget"
  | "basemap-toggle-widget"
  | "locate-widget"
  | "scale-bar-widget"
  | "search-widget"
  | "basemap-gallery-widget"
  | "expand-widget"
  | "compass-widget"
  | "bookmarks-widget"
  | "fullscreen-widget"
  | "zoom-widget"
  | "attribution-widget";

interface ConstructorRewriteSpec {
  kind: CodemodConstructorKind;
  compatSymbol: string;
  arcGisModules: ReadonlySet<string>;
}

const REWRITE_SPECS: readonly ConstructorRewriteSpec[] = [
  {
    kind: "feature-layer",
    compatSymbol: "FeatureLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/FeatureLayer",
      "@arcgis/core/layers/FeatureLayer.js",
    ]),
  },
  {
    kind: "graphics-layer",
    compatSymbol: "GraphicsLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/GraphicsLayer",
      "@arcgis/core/layers/GraphicsLayer.js",
    ]),
  },
  {
    kind: "group-layer",
    compatSymbol: "GroupLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/GroupLayer",
      "@arcgis/core/layers/GroupLayer.js",
    ]),
  },
  {
    kind: "map-image-layer",
    compatSymbol: "MapImageLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/MapImageLayer",
      "@arcgis/core/layers/MapImageLayer.js",
    ]),
  },
  {
    kind: "tile-layer",
    compatSymbol: "TileLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/TileLayer",
      "@arcgis/core/layers/TileLayer.js",
    ]),
  },
  {
    kind: "map",
    compatSymbol: "MapCompat",
    arcGisModules: new Set(["@arcgis/core/Map", "@arcgis/core/Map.js"]),
  },
  {
    kind: "map-view",
    compatSymbol: "MapViewCompat",
    arcGisModules: new Set([
      "@arcgis/core/views/MapView",
      "@arcgis/core/views/MapView.js",
    ]),
  },
  {
    kind: "web-map",
    compatSymbol: "WebMapCompat",
    arcGisModules: new Set(["@arcgis/core/WebMap", "@arcgis/core/WebMap.js"]),
  },
  {
    kind: "scene-view",
    compatSymbol: "SceneViewCompat",
    arcGisModules: new Set([
      "@arcgis/core/views/SceneView",
      "@arcgis/core/views/SceneView.js",
    ]),
  },
  {
    kind: "layer-list",
    compatSymbol: "LayerListCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/LayerList",
      "@arcgis/core/widgets/LayerList.js",
    ]),
  },
  {
    kind: "legend-widget",
    compatSymbol: "LegendCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Legend",
      "@arcgis/core/widgets/Legend.js",
    ]),
  },
  {
    kind: "popup-widget",
    compatSymbol: "PopupCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Popup",
      "@arcgis/core/widgets/Popup.js",
    ]),
  },
  {
    kind: "home-widget",
    compatSymbol: "HomeCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Home",
      "@arcgis/core/widgets/Home.js",
    ]),
  },
  {
    kind: "basemap-toggle-widget",
    compatSymbol: "BasemapToggleCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/BasemapToggle",
      "@arcgis/core/widgets/BasemapToggle.js",
    ]),
  },
  {
    kind: "locate-widget",
    compatSymbol: "LocateCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Locate",
      "@arcgis/core/widgets/Locate.js",
    ]),
  },
  {
    kind: "scale-bar-widget",
    compatSymbol: "ScaleBarCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/ScaleBar",
      "@arcgis/core/widgets/ScaleBar.js",
    ]),
  },
  {
    kind: "search-widget",
    compatSymbol: "SearchCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Search",
      "@arcgis/core/widgets/Search.js",
    ]),
  },
  {
    kind: "basemap-gallery-widget",
    compatSymbol: "BasemapGalleryCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/BasemapGallery",
      "@arcgis/core/widgets/BasemapGallery.js",
    ]),
  },
  {
    kind: "expand-widget",
    compatSymbol: "ExpandCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Expand",
      "@arcgis/core/widgets/Expand.js",
    ]),
  },
  {
    kind: "compass-widget",
    compatSymbol: "CompassCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Compass",
      "@arcgis/core/widgets/Compass.js",
    ]),
  },
  {
    kind: "bookmarks-widget",
    compatSymbol: "BookmarksCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Bookmarks",
      "@arcgis/core/widgets/Bookmarks.js",
    ]),
  },
  {
    kind: "fullscreen-widget",
    compatSymbol: "FullscreenCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Fullscreen",
      "@arcgis/core/widgets/Fullscreen.js",
    ]),
  },
  {
    kind: "zoom-widget",
    compatSymbol: "ZoomCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Zoom",
      "@arcgis/core/widgets/Zoom.js",
    ]),
  },
  {
    kind: "attribution-widget",
    compatSymbol: "AttributionCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Attribution",
      "@arcgis/core/widgets/Attribution.js",
    ]),
  },
];

const TARGET_SUPPORTED_KINDS: Readonly<Record<CodemodTarget, ReadonlySet<CodemodConstructorKind>>> =
  Object.freeze({
    "honua-compat": new Set(REWRITE_SPECS.map((spec) => spec.kind)),
    "esri-leaflet": new Set(["feature-layer", "map-image-layer", "tile-layer"] as const),
  });

export const SUPPORTED_ARCGIS_MODULES: readonly string[] = REWRITE_SPECS.flatMap((spec) =>
  Array.from(spec.arcGisModules),
);
export const SUPPORTED_ARCGIS_MODULE_KIND_BY_PATH: Readonly<Record<string, CodemodConstructorKind>> =
  Object.freeze(buildModuleToKindLookup(REWRITE_SPECS));

const MODULE_TO_SPEC = buildModuleToSpecLookup(REWRITE_SPECS);

export function isKindSupportedForTarget(kind: CodemodConstructorKind, target: CodemodTarget): boolean {
  return TARGET_SUPPORTED_KINDS[target].has(kind);
}

interface TextEdit {
  start: number;
  end: number;
  text: string;
}

interface ArcGisImportBinding {
  kind: CodemodConstructorKind;
  localName: string;
  importStyle: "identifier" | "namespace-default";
  sourceKind: "import" | "require";
}

interface RequireBinding {
  modulePath: string;
  localName: string;
}

export interface MigrationTodo {
  kind: CodemodConstructorKind;
  file: string;
  line: number;
  column: number;
  reason: string;
}

export interface CodemodKindMetrics {
  total: number;
  autoMigrated: number;
  manual: number;
}

export type CodemodMetricsByKind = Record<CodemodConstructorKind, CodemodKindMetrics>;

export interface CodemodMetrics {
  totalCodemodScopedCallSites: number;
  autoMigratedCallSites: number;
  manualCallSites: number;
  byKind: CodemodMetricsByKind;
}

export interface CodemodFileResult {
  file: string;
  rewrittenConstructors: number;
  rewrittenDynamicImports: number;
  addedCompatImport: boolean;
  removedArcGisImports: number;
  annotatedTodoComments: number;
  manualTodos: MigrationTodo[];
}

export interface EsriCompatCodemodResult {
  rootDir: string;
  target: CodemodTarget;
  filesScanned: number;
  filesChanged: number;
  metrics: CodemodMetrics;
  fileResults: CodemodFileResult[];
  manualTodos: MigrationTodo[];
}

export interface EsriCompatCodemodOptions {
  rootDir: string;
  write?: boolean;
  compatImportPath?: string;
  annotateTodos?: boolean;
  target?: CodemodTarget;
}

export function runEsriCompatCodemod(options: EsriCompatCodemodOptions): EsriCompatCodemodResult {
  const rootDir = path.resolve(options.rootDir);
  const files = collectSourceFiles(rootDir);
  const compatImportPath = options.compatImportPath ?? DEFAULT_COMPAT_IMPORT_PATH;
  const annotateTodos = options.annotateTodos ?? false;
  const target = options.target ?? "honua-compat";

  const metrics: CodemodMetrics = {
    totalCodemodScopedCallSites: 0,
    autoMigratedCallSites: 0,
    manualCallSites: 0,
    byKind: createEmptyByKindMetrics(),
  };
  const fileResults: CodemodFileResult[] = [];
  const manualTodos: MigrationTodo[] = [];

  for (const file of files) {
    const source = fs.readFileSync(file, "utf8");
    const fileResult = codemodFile(file, source, compatImportPath, annotateTodos, target);

    for (const kind of fileResult.rewrittenKinds) {
      metrics.byKind[kind].autoMigrated += 1;
      metrics.byKind[kind].total += 1;
      metrics.autoMigratedCallSites += 1;
      metrics.totalCodemodScopedCallSites += 1;
    }
    for (const todo of fileResult.manualTodos) {
      metrics.byKind[todo.kind].manual += 1;
      metrics.byKind[todo.kind].total += 1;
      metrics.manualCallSites += 1;
      metrics.totalCodemodScopedCallSites += 1;
    }
    manualTodos.push(...fileResult.manualTodos);

    const hasChanges =
      fileResult.rewrittenConstructors > 0 ||
      fileResult.rewrittenDynamicImports > 0 ||
      fileResult.addedCompatImport ||
      fileResult.removedArcGisImports > 0 ||
      fileResult.annotatedTodoComments > 0;
    if (hasChanges) {
      if (options.write) {
        fs.writeFileSync(file, fileResult.nextSource, "utf8");
      }
      fileResults.push({
        file,
        rewrittenConstructors: fileResult.rewrittenConstructors,
        rewrittenDynamicImports: fileResult.rewrittenDynamicImports,
        addedCompatImport: fileResult.addedCompatImport,
        removedArcGisImports: fileResult.removedArcGisImports,
        annotatedTodoComments: fileResult.annotatedTodoComments,
        manualTodos: fileResult.manualTodos,
      });
    } else if (fileResult.manualTodos.length > 0) {
      fileResults.push({
        file,
        rewrittenConstructors: 0,
        rewrittenDynamicImports: 0,
        addedCompatImport: false,
        removedArcGisImports: 0,
        annotatedTodoComments: 0,
        manualTodos: fileResult.manualTodos,
      });
    }
  }

  return {
    rootDir,
    target,
    filesScanned: files.length,
    filesChanged: fileResults.filter(
      (item) =>
        item.rewrittenConstructors > 0 ||
        item.rewrittenDynamicImports > 0 ||
        item.addedCompatImport ||
        item.removedArcGisImports > 0 ||
        item.annotatedTodoComments > 0,
    ).length,
    metrics,
    fileResults: fileResults.sort((a, b) => a.file.localeCompare(b.file)),
    manualTodos: manualTodos.sort(compareTodos),
  };
}

function codemodFile(
  file: string,
  source: string,
  compatImportPath: string,
  annotateTodos: boolean,
  target: CodemodTarget,
): {
  nextSource: string;
  rewrittenConstructors: number;
  rewrittenDynamicImports: number;
  rewrittenKinds: CodemodConstructorKind[];
  addedCompatImport: boolean;
  removedArcGisImports: number;
  annotatedTodoComments: number;
  manualTodos: MigrationTodo[];
} {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  const imports = collectSupportedImports(sourceFile);

  const importsByLocalName = new Map<string, ArcGisImportBinding>();
  for (const importBinding of imports) {
    if (!importsByLocalName.has(importBinding.localName)) {
      importsByLocalName.set(importBinding.localName, importBinding);
    }
  }

  const constructorEdits: TextEdit[] = [];
  const dynamicImportEdits: TextEdit[] = [];
  const rewrittenKinds: CodemodConstructorKind[] = [];
  const manualTodos: MigrationTodo[] = [];
  const todoCommentEdits: TextEdit[] = [];
  const requiredCompatSymbols = new Set<string>();
  const requiresEsriLeafletImport = { value: false };
  const esriLeafletNamespaceAlias =
    findNamespaceImportAlias(sourceFile, ESRI_LEAFLET_IMPORT_PATH) ?? ESRI_LEAFLET_NAMESPACE;
  const fileExtension = path.extname(file).toLowerCase();
  const isCommonJsModule = fileExtension === ".cjs" || hasCommonJsExportMarkers(source);

  walk(sourceFile, (node) => {
    if (isArcGisDynamicImportCall(node)) {
      const firstArg = node.arguments[0];
      if (!ts.isStringLiteral(firstArg)) {
        return;
      }

      const modulePath = firstArg.text;
      const spec = MODULE_TO_SPEC.get(modulePath);
      if (spec) {
        if (target === "honua-compat") {
          dynamicImportEdits.push({
            start: node.getStart(sourceFile),
            end: node.getEnd(),
            text: buildCompatDynamicImportExpression(compatImportPath, spec.compatSymbol),
          });
          rewrittenKinds.push(spec.kind);
          return;
        }

        const targetExpression = buildEsriLeafletDynamicImportExpression(spec.kind, esriLeafletNamespaceAlias);
        if (targetExpression) {
          dynamicImportEdits.push({
            start: node.getStart(sourceFile),
            end: node.getEnd(),
            text: targetExpression,
          });
          rewrittenKinds.push(spec.kind);
          requiresEsriLeafletImport.value = true;
          return;
        }

        const nodeStart = node.getStart(sourceFile);
        const location = sourceFile.getLineAndCharacterOfPosition(nodeStart);
        manualTodos.push({
          kind: spec.kind,
          file,
          line: location.line + 1,
          column: location.character + 1,
          reason: ESRI_LEAFLET_UNSUPPORTED_DYNAMIC_IMPORT_REASON,
        });
        if (annotateTodos) {
          const lineStart = findLineStartOffset(source, nodeStart);
          if (shouldInsertTodoComment(source, lineStart, nodeStart)) {
            todoCommentEdits.push({
              start: lineStart,
              end: lineStart,
              text: `// ${TODO_MARKER}[${spec.kind}]: ${ESRI_LEAFLET_UNSUPPORTED_DYNAMIC_IMPORT_REASON}\n`,
            });
          }
        }
      }
      return;
    }

    if (!ts.isNewExpression(node)) {
      return;
    }

    const rewriteTarget = resolveConstructorRewriteTarget(node.expression, sourceFile, importsByLocalName);
    if (!rewriteTarget) {
      return;
    }

    const importBinding = rewriteTarget.binding;
    if (isCommonJsModule && importBinding.sourceKind === "require") {
      const nodeStart = node.getStart(sourceFile);
      const location = sourceFile.getLineAndCharacterOfPosition(nodeStart);
      manualTodos.push({
        kind: importBinding.kind,
        file,
        line: location.line + 1,
        column: location.character + 1,
        reason: CJS_REQUIRE_MANUAL_REASON,
      });
      if (annotateTodos) {
        const lineStart = findLineStartOffset(source, nodeStart);
        if (shouldInsertTodoComment(source, lineStart, nodeStart)) {
          todoCommentEdits.push({
            start: lineStart,
            end: lineStart,
            text: `// ${TODO_MARKER}[${importBinding.kind}]: ${CJS_REQUIRE_MANUAL_REASON}\n`,
          });
        }
      }
      return;
    }

    const safeCheck = isSafeConstructorCall(importBinding.kind, node);
    if (safeCheck.ok) {
      if (target === "honua-compat") {
        const spec = specForKind(importBinding.kind);
        requiredCompatSymbols.add(spec.compatSymbol);
        constructorEdits.push({
          start: rewriteTarget.start,
          end: rewriteTarget.end,
          text: spec.compatSymbol,
        });
        rewrittenKinds.push(importBinding.kind);
        return;
      }

      const replacement = buildEsriLeafletConstructorExpression(
        importBinding.kind,
        node,
        sourceFile,
        esriLeafletNamespaceAlias,
      );
      if (replacement) {
        constructorEdits.push({
          start: node.getStart(sourceFile),
          end: node.getEnd(),
          text: replacement,
        });
        rewrittenKinds.push(importBinding.kind);
        requiresEsriLeafletImport.value = true;
        return;
      }

      const nodeStart = node.getStart(sourceFile);
      const location = sourceFile.getLineAndCharacterOfPosition(nodeStart);
      manualTodos.push({
        kind: importBinding.kind,
        file,
        line: location.line + 1,
        column: location.character + 1,
        reason: ESRI_LEAFLET_UNSUPPORTED_CONSTRUCTOR_REASON,
      });
      if (annotateTodos) {
        const lineStart = findLineStartOffset(source, nodeStart);
        if (shouldInsertTodoComment(source, lineStart, nodeStart)) {
          todoCommentEdits.push({
            start: lineStart,
            end: lineStart,
            text: `// ${TODO_MARKER}[${importBinding.kind}]: ${ESRI_LEAFLET_UNSUPPORTED_CONSTRUCTOR_REASON}\n`,
          });
        }
      }
      return;
    }

    const nodeStart = node.getStart(sourceFile);
    const location = sourceFile.getLineAndCharacterOfPosition(nodeStart);
    manualTodos.push({
      kind: importBinding.kind,
      file,
      line: location.line + 1,
      column: location.character + 1,
      reason: safeCheck.reason,
    });
    if (annotateTodos) {
      const lineStart = findLineStartOffset(source, nodeStart);
      if (shouldInsertTodoComment(source, lineStart, nodeStart)) {
        todoCommentEdits.push({
          start: lineStart,
          end: lineStart,
          text: `// ${TODO_MARKER}[${importBinding.kind}]: ${safeCheck.reason}\n`,
        });
      }
    }
  });

  if (constructorEdits.length === 0 && dynamicImportEdits.length === 0) {
    return {
      nextSource: applyTextEdits(source, todoCommentEdits),
      rewrittenConstructors: 0,
      rewrittenDynamicImports: 0,
      rewrittenKinds: [],
      addedCompatImport: false,
      removedArcGisImports: 0,
      annotatedTodoComments: todoCommentEdits.length,
      manualTodos: manualTodos.sort(compareTodos),
    };
  }

  let transformed = applyTextEdits(source, [
    ...constructorEdits,
    ...dynamicImportEdits,
    ...todoCommentEdits,
  ]);
  const removedArcGisImports = removeUnusedArcGisImports(file, transformed);
  transformed = removedArcGisImports.nextSource;

  let addedCompatImport = false;
  if (target === "honua-compat") {
    const compatSymbols = Array.from(requiredCompatSymbols).sort();
    const compatImportResult = ensureCompatNamedImports(
      file,
      transformed,
      compatSymbols,
      compatImportPath,
    );
    transformed = compatImportResult.nextSource;
    addedCompatImport = compatImportResult.changed;
  } else if (requiresEsriLeafletImport.value) {
    const esriLeafletImportResult = ensureNamespaceImport(
      file,
      transformed,
      ESRI_LEAFLET_IMPORT_PATH,
      esriLeafletNamespaceAlias,
    );
    transformed = esriLeafletImportResult.nextSource;
    addedCompatImport = esriLeafletImportResult.changed;
  }

  return {
    nextSource: transformed,
    rewrittenConstructors: constructorEdits.length,
    rewrittenDynamicImports: dynamicImportEdits.length,
    rewrittenKinds,
    addedCompatImport,
    removedArcGisImports: removedArcGisImports.removedCount,
    annotatedTodoComments: todoCommentEdits.length,
    manualTodos: manualTodos.sort(compareTodos),
  };
}

function hasCommonJsExportMarkers(source: string): boolean {
  return /\bmodule\.exports\b/.test(source) || /\bexports\.[A-Za-z_$][A-Za-z0-9_$]*\b/.test(source);
}

function buildModuleToSpecLookup(specs: readonly ConstructorRewriteSpec[]): Map<string, ConstructorRewriteSpec> {
  const result = new Map<string, ConstructorRewriteSpec>();
  for (const spec of specs) {
    for (const modulePath of spec.arcGisModules) {
      result.set(modulePath, spec);
    }
  }
  return result;
}

function buildModuleToKindLookup(
  specs: readonly ConstructorRewriteSpec[],
): Record<string, CodemodConstructorKind> {
  const result: Record<string, CodemodConstructorKind> = {};
  for (const spec of specs) {
    for (const modulePath of spec.arcGisModules) {
      result[modulePath] = spec.kind;
    }
  }
  return result;
}

function createEmptyByKindMetrics(): CodemodMetricsByKind {
  return {
    "feature-layer": { total: 0, autoMigrated: 0, manual: 0 },
    "graphics-layer": { total: 0, autoMigrated: 0, manual: 0 },
    "group-layer": { total: 0, autoMigrated: 0, manual: 0 },
    "map-image-layer": { total: 0, autoMigrated: 0, manual: 0 },
    "tile-layer": { total: 0, autoMigrated: 0, manual: 0 },
    map: { total: 0, autoMigrated: 0, manual: 0 },
    "map-view": { total: 0, autoMigrated: 0, manual: 0 },
    "scene-view": { total: 0, autoMigrated: 0, manual: 0 },
    "web-map": { total: 0, autoMigrated: 0, manual: 0 },
    "layer-list": { total: 0, autoMigrated: 0, manual: 0 },
    "legend-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "popup-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "home-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "basemap-toggle-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "locate-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "scale-bar-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "search-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "basemap-gallery-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "expand-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "compass-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "bookmarks-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "fullscreen-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "zoom-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "attribution-widget": { total: 0, autoMigrated: 0, manual: 0 },
  };
}

function collectSupportedImports(sourceFile: ts.SourceFile): ArcGisImportBinding[] {
  const result: ArcGisImportBinding[] = [];

  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }

    const spec = MODULE_TO_SPEC.get(statement.moduleSpecifier.text);
    if (!spec) {
      continue;
    }

    const importClause = statement.importClause;
    if (!importClause) {
      continue;
    }

    if (importClause.name) {
      result.push({
        kind: spec.kind,
        localName: importClause.name.text,
        importStyle: "identifier",
        sourceKind: "import",
      });
    }

    const namedBindings = importClause.namedBindings;
    if (namedBindings && ts.isNamedImports(namedBindings)) {
      for (const element of namedBindings.elements) {
        const importedName = element.propertyName?.text ?? element.name.text;
        if (importedName === "default") {
          result.push({
            kind: spec.kind,
            localName: element.name.text,
            importStyle: "identifier",
            sourceKind: "import",
          });
        }
      }
    }
    if (namedBindings && ts.isNamespaceImport(namedBindings)) {
      result.push({
        kind: spec.kind,
        localName: namedBindings.name.text,
        importStyle: "namespace-default",
        sourceKind: "import",
      });
    }
  }

  for (const statement of sourceFile.statements) {
    if (!ts.isVariableStatement(statement)) {
      continue;
    }

    for (const declaration of statement.declarationList.declarations) {
      const requireBinding = extractRequireBindingFromDeclaration(declaration);
      if (!requireBinding) {
        continue;
      }

      const spec = MODULE_TO_SPEC.get(requireBinding.modulePath);
      if (!spec) {
        continue;
      }

      result.push({
        kind: spec.kind,
        localName: requireBinding.localName,
        importStyle: "identifier",
        sourceKind: "require",
      });
    }
  }

  return result;
}

function resolveConstructorRewriteTarget(
  expression: ts.Expression,
  sourceFile: ts.SourceFile,
  importsByLocalName: ReadonlyMap<string, ArcGisImportBinding>,
): { binding: ArcGisImportBinding; start: number; end: number } | undefined {
  if (ts.isIdentifier(expression)) {
    const binding = importsByLocalName.get(expression.text);
    if (!binding || binding.importStyle !== "identifier") {
      return undefined;
    }

    return {
      binding,
      start: expression.getStart(sourceFile),
      end: expression.getEnd(),
    };
  }

  if (
    ts.isPropertyAccessExpression(expression) &&
    expression.name.text === "default" &&
    ts.isIdentifier(expression.expression)
  ) {
    const binding = importsByLocalName.get(expression.expression.text);
    if (!binding || binding.importStyle !== "namespace-default") {
      return undefined;
    }

    return {
      binding,
      start: expression.getStart(sourceFile),
      end: expression.getEnd(),
    };
  }

  return undefined;
}

function ensureCompatNamedImports(
  file: string,
  source: string,
  symbols: readonly string[],
  importPath: string,
): { nextSource: string; changed: boolean } {
  if (symbols.length === 0) {
    return { nextSource: source, changed: false };
  }

  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (statement.moduleSpecifier.text !== importPath) {
      continue;
    }

    const importClause = statement.importClause;
    const namedBindings = importClause?.namedBindings;
    if (namedBindings && ts.isNamedImports(namedBindings)) {
      const existingSymbols = namedBindings.elements.map((element) => element.name.text);
      const existingSet = new Set(existingSymbols);
      const missing = symbols.filter((symbol) => !existingSet.has(symbol));
      if (missing.length === 0) {
        return { nextSource: source, changed: false };
      }

      const mergedSymbols = [...existingSymbols, ...missing];
      const replacement = buildNamedImportText(
        importPath,
        importClause?.name?.text,
        mergedSymbols,
      );

      const nextSource = applyTextEdits(source, [
        {
          start: statement.getStart(sourceFile),
          end: statement.getEnd(),
          text: replacement,
        },
      ]);
      return { nextSource, changed: true };
    }

    const importLine = `${buildNamedImportText(importPath, undefined, symbols)}\n`;
    const insertion = statement.getEnd();
    const nextSource = `${source.slice(0, insertion)}\n${importLine}${source.slice(insertion)}`;
    return { nextSource, changed: true };
  }

  const insertionIndex = findImportInsertionIndex(sourceFile);
  const importLine = buildNamedImportText(importPath, undefined, symbols);
  const prefix = source.slice(0, insertionIndex);
  const suffix = source.slice(insertionIndex);
  const needsLeadingNewline = prefix.length > 0 && !prefix.endsWith("\n");
  const leading = needsLeadingNewline ? "\n" : "";
  const needsTrailingNewline =
    suffix.length > 0 && !suffix.startsWith("\n") && !importLine.endsWith("\n");
  const trailing = needsTrailingNewline ? "\n" : "";

  return {
    nextSource: `${prefix}${leading}${importLine}${trailing}${suffix}`,
    changed: true,
  };
}

function findNamespaceImportAlias(sourceFile: ts.SourceFile, importPath: string): string | undefined {
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (statement.moduleSpecifier.text !== importPath) {
      continue;
    }

    const namedBindings = statement.importClause?.namedBindings;
    if (namedBindings && ts.isNamespaceImport(namedBindings)) {
      return namedBindings.name.text;
    }
  }

  return undefined;
}

function ensureNamespaceImport(
  file: string,
  source: string,
  importPath: string,
  namespaceAlias: string,
): { nextSource: string; changed: boolean } {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (statement.moduleSpecifier.text !== importPath) {
      continue;
    }

    const namedBindings = statement.importClause?.namedBindings;
    if (namedBindings && ts.isNamespaceImport(namedBindings) && namedBindings.name.text === namespaceAlias) {
      return { nextSource: source, changed: false };
    }

    const importLine = `import * as ${namespaceAlias} from "${importPath}";\n`;
    const insertion = statement.getEnd();
    return {
      nextSource: `${source.slice(0, insertion)}\n${importLine}${source.slice(insertion)}`,
      changed: true,
    };
  }

  const insertionIndex = findImportInsertionIndex(sourceFile);
  const importLine = `import * as ${namespaceAlias} from "${importPath}";`;
  const prefix = source.slice(0, insertionIndex);
  const suffix = source.slice(insertionIndex);
  const needsLeadingNewline = prefix.length > 0 && !prefix.endsWith("\n");
  const leading = needsLeadingNewline ? "\n" : "";
  const needsTrailingNewline =
    suffix.length > 0 && !suffix.startsWith("\n") && !importLine.endsWith("\n");
  const trailing = needsTrailingNewline ? "\n" : "";

  return {
    nextSource: `${prefix}${leading}${importLine}${trailing}${suffix}`,
    changed: true,
  };
}

function buildNamedImportText(
  importPath: string,
  defaultImport: string | undefined,
  namedImports: readonly string[],
): string {
  const uniqueNamed = Array.from(new Set(namedImports));
  const namedImportText = `{ ${uniqueNamed.join(", ")} }`;
  if (defaultImport) {
    return `import ${defaultImport}, ${namedImportText} from "${importPath}";`;
  }

  return `import ${namedImportText} from "${importPath}";`;
}

function findImportInsertionIndex(sourceFile: ts.SourceFile): number {
  let index = 0;
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement)) {
      break;
    }
    index = statement.end;
  }
  return index;
}

function isArcGisDynamicImportCall(node: ts.Node): node is ts.CallExpression {
  if (!ts.isCallExpression(node)) {
    return false;
  }
  if (node.expression.kind !== ts.SyntaxKind.ImportKeyword) {
    return false;
  }
  return node.arguments.length === 1;
}

function buildCompatDynamicImportExpression(compatImportPath: string, compatSymbol: string): string {
  return `import("${compatImportPath}").then((m) => ({ default: m.${compatSymbol} }))`;
}

function buildEsriLeafletConstructorExpression(
  kind: CodemodConstructorKind,
  node: ts.NewExpression,
  sourceFile: ts.SourceFile,
  namespaceAlias: string,
): string | undefined {
  const method = esriLeafletMethodForKind(kind);
  if (!method) {
    return undefined;
  }

  const argsText = node.arguments?.map((arg) => arg.getText(sourceFile)).join(", ") ?? "";
  return `${namespaceAlias}.${method}(${argsText})`;
}

function buildEsriLeafletDynamicImportExpression(
  kind: CodemodConstructorKind,
  namespaceAlias: string,
): string | undefined {
  const method = esriLeafletMethodForKind(kind);
  if (!method) {
    return undefined;
  }

  return `Promise.resolve({ default: ${namespaceAlias}.${method} })`;
}

function esriLeafletMethodForKind(kind: CodemodConstructorKind): string | undefined {
  switch (kind) {
    case "feature-layer":
      return "featureLayer";
    case "map-image-layer":
      return "dynamicMapLayer";
    case "tile-layer":
      return "tiledMapLayer";
    default:
      return undefined;
  }
}

function removeUnusedArcGisImports(
  file: string,
  source: string,
): { nextSource: string; removedCount: number } {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  const removals: TextEdit[] = [];

  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (!MODULE_TO_SPEC.has(statement.moduleSpecifier.text)) {
      continue;
    }

    const importClause = statement.importClause;
    if (!importClause) {
      continue;
    }

    const localIdentifiers = extractImportClauseLocalIdentifiers(importClause);
    if (localIdentifiers.length === 0) {
      continue;
    }

    const hasReferences = localIdentifiers.some(
      (identifier) => countIdentifierUsagesExcludingImports(sourceFile, identifier) > 0,
    );
    if (hasReferences) {
      continue;
    }

    const bounds = expandToFullLine(source, statement.getStart(sourceFile), statement.getEnd());
    removals.push({
      start: bounds.start,
      end: bounds.end,
      text: "",
    });
  }

  for (const statement of sourceFile.statements) {
    if (!ts.isVariableStatement(statement)) {
      continue;
    }

    if (statement.declarationList.declarations.length !== 1) {
      continue;
    }

    const declaration = statement.declarationList.declarations[0];
    const requireBinding = extractRequireBindingFromDeclaration(declaration);
    if (!requireBinding) {
      continue;
    }

    if (!MODULE_TO_SPEC.has(requireBinding.modulePath)) {
      continue;
    }

    const references = countIdentifierUsagesExcludingImportsAndDefinitions(
      sourceFile,
      requireBinding.localName,
    );
    if (references > 0) {
      continue;
    }

    const bounds = expandToFullLine(source, statement.getStart(sourceFile), statement.getEnd());
    removals.push({
      start: bounds.start,
      end: bounds.end,
      text: "",
    });
  }

  if (removals.length === 0) {
    return { nextSource: source, removedCount: 0 };
  }

  return {
    nextSource: applyTextEdits(source, removals),
    removedCount: removals.length,
  };
}

function extractModulePathFromRequireInitializer(initializer: ts.Expression): string | undefined {
  if (ts.isCallExpression(initializer) && initializer.arguments.length === 1) {
    if (
      ts.isIdentifier(initializer.expression) &&
      initializer.expression.text === "require" &&
      ts.isStringLiteral(initializer.arguments[0])
    ) {
      return initializer.arguments[0].text;
    }
    return undefined;
  }

  if (
    ts.isPropertyAccessExpression(initializer) &&
    initializer.name.text === "default" &&
    ts.isCallExpression(initializer.expression) &&
    initializer.expression.arguments.length === 1 &&
    ts.isIdentifier(initializer.expression.expression) &&
    initializer.expression.expression.text === "require" &&
    ts.isStringLiteral(initializer.expression.arguments[0])
  ) {
    return initializer.expression.arguments[0].text;
  }

  return undefined;
}

function extractRequireBindingFromDeclaration(
  declaration: ts.VariableDeclaration,
): RequireBinding | undefined {
  if (!declaration.initializer) {
    return undefined;
  }

  const modulePath = extractModulePathFromRequireInitializer(declaration.initializer);
  if (!modulePath) {
    return undefined;
  }

  if (ts.isIdentifier(declaration.name)) {
    return {
      modulePath,
      localName: declaration.name.text,
    };
  }

  if (!ts.isObjectBindingPattern(declaration.name)) {
    return undefined;
  }

  for (const element of declaration.name.elements) {
    let propertyNameText: string | undefined;
    if (!element.propertyName) {
      propertyNameText = ts.isIdentifier(element.name) ? element.name.text : undefined;
    } else if (ts.isIdentifier(element.propertyName)) {
      propertyNameText = element.propertyName.text;
    } else {
      propertyNameText = element.propertyName.getText();
    }
    if (propertyNameText !== "default") {
      continue;
    }
    if (!ts.isIdentifier(element.name)) {
      continue;
    }
    return {
      modulePath,
      localName: element.name.text,
    };
  }

  return undefined;
}

function extractImportClauseLocalIdentifiers(importClause: ts.ImportClause): string[] {
  const names: string[] = [];
  if (importClause.name) {
    names.push(importClause.name.text);
  }

  const namedBindings = importClause.namedBindings;
  if (!namedBindings) {
    return names;
  }

  if (ts.isNamespaceImport(namedBindings)) {
    names.push(namedBindings.name.text);
    return names;
  }

  for (const element of namedBindings.elements) {
    names.push(element.name.text);
  }

  return names;
}

function countIdentifierUsagesExcludingImports(sourceFile: ts.SourceFile, name: string): number {
  let count = 0;

  walk(sourceFile, (node) => {
    if (!ts.isIdentifier(node) || node.text !== name) {
      return;
    }
    if (isInImportContext(node)) {
      return;
    }
    count += 1;
  });

  return count;
}

function countIdentifierUsagesExcludingImportsAndDefinitions(
  sourceFile: ts.SourceFile,
  name: string,
): number {
  let count = 0;

  walk(sourceFile, (node) => {
    if (!ts.isIdentifier(node) || node.text !== name) {
      return;
    }
    if (isInImportContext(node)) {
      return;
    }
    if (isVariableDeclarationName(node)) {
      return;
    }
    count += 1;
  });

  return count;
}

function isInImportContext(node: ts.Identifier): boolean {
  let current: ts.Node | undefined = node;
  while (current) {
    if (
      ts.isImportClause(current) ||
      ts.isImportDeclaration(current) ||
      ts.isImportSpecifier(current) ||
      ts.isNamespaceImport(current) ||
      ts.isNamedImports(current)
    ) {
      return true;
    }
    current = current.parent;
  }
  return false;
}

function isVariableDeclarationName(node: ts.Identifier): boolean {
  return (
    (ts.isVariableDeclaration(node.parent) && node.parent.name === node) ||
    (ts.isBindingElement(node.parent) && node.parent.name === node)
  );
}

function expandToFullLine(source: string, start: number, end: number): { start: number; end: number } {
  let expandedStart = start;
  while (expandedStart > 0 && source[expandedStart - 1] !== "\n") {
    expandedStart -= 1;
  }

  let expandedEnd = end;
  while (expandedEnd < source.length && source[expandedEnd] !== "\n") {
    expandedEnd += 1;
  }
  if (expandedEnd < source.length && source[expandedEnd] === "\n") {
    expandedEnd += 1;
  }

  return { start: expandedStart, end: expandedEnd };
}

function findLineStartOffset(source: string, position: number): number {
  let start = position;
  while (start > 0 && source[start - 1] !== "\n") {
    start -= 1;
  }
  return start;
}

function shouldInsertTodoComment(source: string, lineStart: number, nodeStart: number): boolean {
  const currentPrefix = source.slice(lineStart, nodeStart);
  if (currentPrefix.includes(TODO_MARKER)) {
    return false;
  }

  if (lineStart === 0) {
    return true;
  }

  const previousLineEnd = lineStart - 1;
  const previousLineStart = findLineStartOffset(source, previousLineEnd);
  const previousLine = source.slice(previousLineStart, lineStart);
  return !previousLine.includes(TODO_MARKER);
}

function applyTextEdits(source: string, edits: readonly TextEdit[]): string {
  const sorted = edits
    .slice()
    .sort((a, b) => (a.start === b.start ? b.end - a.end : b.start - a.start));

  let nextSource = source;
  for (const edit of sorted) {
    nextSource = `${nextSource.slice(0, edit.start)}${edit.text}${nextSource.slice(edit.end)}`;
  }
  return nextSource;
}

function isSafeConstructorCall(
  kind: CodemodConstructorKind,
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  switch (kind) {
    case "feature-layer":
      return isSafeFeatureLayerCompatCall(node);
    case "graphics-layer":
      return isSafeGraphicsLayerCompatCall(node);
    case "group-layer":
      return isSafeGroupLayerCompatCall(node);
    case "map-image-layer":
      return isSafeMapImageLayerCompatCall(node);
    case "tile-layer":
      return isSafeTileLayerCompatCall(node);
    case "map":
      return isSafeMapCompatCall(node);
    case "map-view":
      return isSafeMapViewCompatCall(node);
    case "scene-view":
      return isSafeSceneViewCompatCall(node);
    case "web-map":
      return isSafeWebMapCompatCall(node);
    case "layer-list":
      return isSafeLayerListCompatCall(node);
    case "legend-widget":
      return isSafeLegendWidgetCompatCall(node);
    case "popup-widget":
      return isSafePopupWidgetCompatCall(node);
    case "home-widget":
      return isSafeHomeWidgetCompatCall(node);
    case "basemap-toggle-widget":
      return isSafeBasemapToggleWidgetCompatCall(node);
    case "locate-widget":
      return isSafeLocateWidgetCompatCall(node);
    case "scale-bar-widget":
      return isSafeScaleBarWidgetCompatCall(node);
    case "search-widget":
      return isSafeSearchWidgetCompatCall(node);
    case "basemap-gallery-widget":
      return isSafeBasemapGalleryWidgetCompatCall(node);
    case "expand-widget":
      return isSafeExpandWidgetCompatCall(node);
    case "compass-widget":
      return isSafeCompassWidgetCompatCall(node);
    case "bookmarks-widget":
      return isSafeBookmarksWidgetCompatCall(node);
    case "fullscreen-widget":
      return isSafeFullscreenWidgetCompatCall(node);
    case "zoom-widget":
      return isSafeZoomWidgetCompatCall(node);
    case "attribution-widget":
      return isSafeAttributionWidgetCompatCall(node);
    default:
      return { ok: false, reason: "Unsupported ArcGIS constructor usage." };
  }
}

function isSafeFeatureLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length !== 1) {
    return {
      ok: false,
      reason: "FeatureLayer constructor is not a single object-literal argument.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "FeatureLayer constructor argument is not an object literal.",
    };
  }

  let hasUrlOption = false;
  const allowed = new Set(["url", "outFields", "definitionExpression"]);

  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "FeatureLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (name === "url") {
      hasUrlOption = true;
    }

    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "FeatureLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  if (!hasUrlOption) {
    return {
      ok: false,
      reason: "FeatureLayer options missing required url property; requires manual migration.",
    };
  }

  return { ok: true };
}

function isSafeMapImageLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length !== 1) {
    return {
      ok: false,
      reason: "MapImageLayer constructor is not a single object-literal argument.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "MapImageLayer constructor argument is not an object literal.",
    };
  }

  let hasUrlOption = false;
  const allowed = new Set(["url", "sublayers", "opacity", "visible"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "MapImageLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (name === "url") {
      hasUrlOption = true;
    }

    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "MapImageLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  if (!hasUrlOption) {
    return {
      ok: false,
      reason: "MapImageLayer options missing required url property; requires manual migration.",
    };
  }

  return { ok: true };
}

function isSafeTileLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length !== 1) {
    return {
      ok: false,
      reason: "TileLayer constructor is not a single object-literal argument.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "TileLayer constructor argument is not an object literal.",
    };
  }

  let hasUrlOption = false;
  const allowed = new Set(["url", "opacity", "visible"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "TileLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (name === "url") {
      hasUrlOption = true;
    }

    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "TileLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  if (!hasUrlOption) {
    return {
      ok: false,
      reason: "TileLayer options missing required url property; requires manual migration.",
    };
  }

  return { ok: true };
}

function isSafeGraphicsLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "GraphicsLayer constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "GraphicsLayer constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["graphics", "id", "title", "visible", "opacity", "listMode"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "GraphicsLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "GraphicsLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeGroupLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "GroupLayer constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "GroupLayer constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "layers",
    "id",
    "title",
    "visible",
    "opacity",
    "listMode",
    "visibilityMode",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "GroupLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "GroupLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeMapCompatCall(node: ts.NewExpression): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Map constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Map constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["basemap", "layers"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Map options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Map options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeMapViewCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "MapView constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "MapView constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["map", "container", "center", "zoom"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "MapView options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "MapView options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeWebMapCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "WebMap constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "WebMap constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["portalItem", "basemap", "layers"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "WebMap options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "WebMap options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSceneViewCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "SceneView constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "SceneView constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["map", "container", "center", "zoom", "camera", "qualityProfile", "viewingMode"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "SceneView options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "SceneView options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeLayerListCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "LayerList constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "LayerList constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "LayerList options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "LayerList options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeLegendWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Legend constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Legend constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "layers", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Legend options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Legend options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafePopupWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Popup constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Popup constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "autoOpenEnabled", "dockEnabled", "dockOptions"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Popup options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Popup options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeHomeWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Home constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Home constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Home options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Home options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeBasemapToggleWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "BasemapToggle constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "BasemapToggle constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "container", "nextBasemap"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "BasemapToggle options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "BasemapToggle options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeLocateWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Locate constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Locate constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Locate options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Locate options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeScaleBarWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "ScaleBar constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "ScaleBar constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "unit"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "ScaleBar options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "ScaleBar options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSearchWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Search constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Search constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "sources", "includeDefaultSources", "autoNavigate"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Search options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Search options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeBasemapGalleryWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "BasemapGallery constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "BasemapGallery constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "container", "source"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "BasemapGallery options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "BasemapGallery options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeExpandWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Expand constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Expand constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "content", "expanded", "mode", "group"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Expand options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Expand options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeCompassWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Compass constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Compass constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Compass options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Compass options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeBookmarksWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Bookmarks constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Bookmarks constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "bookmarks"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Bookmarks options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Bookmarks options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeFullscreenWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Fullscreen constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Fullscreen constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "element"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Fullscreen options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Fullscreen options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeZoomWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Zoom constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Zoom constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "layout"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Zoom options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Zoom options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeAttributionWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Attribution constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Attribution constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "container", "itemDelimiter", "attributions"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Attribution options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Attribution options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function getPropertyNameText(name: ts.PropertyName): string | undefined {
  if (ts.isIdentifier(name)) {
    return name.text;
  }
  if (ts.isStringLiteral(name) || ts.isNoSubstitutionTemplateLiteral(name)) {
    return name.text;
  }
  return undefined;
}

function isAssignableObjectProperty(property: ts.ObjectLiteralElementLike): boolean {
  return ts.isPropertyAssignment(property) || ts.isShorthandPropertyAssignment(property);
}

function getObjectPropertyName(property: ts.ObjectLiteralElementLike): string | undefined {
  if (ts.isPropertyAssignment(property)) {
    return getPropertyNameText(property.name);
  }
  if (ts.isShorthandPropertyAssignment(property)) {
    return property.name.text;
  }
  return undefined;
}

function collectSourceFiles(rootDir: string): string[] {
  const queue = [rootDir];
  const result: string[] = [];

  while (queue.length > 0) {
    const current = queue.pop()!;
    const entries = fs.readdirSync(current, { withFileTypes: true });
    for (const entry of entries) {
      const fullPath = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (!SKIP_DIRS.has(entry.name)) {
          queue.push(fullPath);
        }
        continue;
      }

      if (SOURCE_EXTENSIONS.has(path.extname(entry.name))) {
        result.push(fullPath);
      }
    }
  }

  return result;
}

function walk(node: ts.Node, visit: (node: ts.Node) => void): void {
  visit(node);
  node.forEachChild((child) => walk(child, visit));
}

function specForKind(kind: CodemodConstructorKind): ConstructorRewriteSpec {
  for (const spec of REWRITE_SPECS) {
    if (spec.kind === kind) {
      return spec;
    }
  }
  throw new Error(`Unknown constructor rewrite kind: ${kind}`);
}

function compareTodos(a: MigrationTodo, b: MigrationTodo): number {
  const fileCmp = a.file.localeCompare(b.file);
  if (fileCmp !== 0) {
    return fileCmp;
  }
  if (a.line !== b.line) {
    return a.line - b.line;
  }
  if (a.column !== b.column) {
    return a.column - b.column;
  }
  if (a.kind !== b.kind) {
    return a.kind.localeCompare(b.kind);
  }
  return a.reason.localeCompare(b.reason);
}
