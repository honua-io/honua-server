import fs from "node:fs";
import path from "node:path";
import ts from "typescript";

const SOURCE_EXTENSIONS = new Set([".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"]);
const SKIP_DIRS = new Set(["node_modules", "dist", ".git"]);
const DEFAULT_COMPAT_IMPORT_PATH = "@honua/sdk-esri-compat";
const TODO_MARKER = "TODO(honua-migrate)";
const CJS_REQUIRE_MANUAL_REASON =
  "CommonJS require constructors are not auto-migrated; convert the module to ESM and rerun.";

export type CodemodConstructorKind =
  | "feature-layer"
  | "map-image-layer"
  | "map"
  | "map-view"
  | "scene-view"
  | "web-map";

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
    kind: "map-image-layer",
    compatSymbol: "MapImageLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/MapImageLayer",
      "@arcgis/core/layers/MapImageLayer.js",
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
];

export const SUPPORTED_ARCGIS_MODULES: readonly string[] = REWRITE_SPECS.flatMap((spec) =>
  Array.from(spec.arcGisModules),
);
export const SUPPORTED_ARCGIS_MODULE_KIND_BY_PATH: Readonly<Record<string, CodemodConstructorKind>> =
  Object.freeze(buildModuleToKindLookup(REWRITE_SPECS));

const MODULE_TO_SPEC = buildModuleToSpecLookup(REWRITE_SPECS);

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
}

export function runEsriCompatCodemod(options: EsriCompatCodemodOptions): EsriCompatCodemodResult {
  const rootDir = path.resolve(options.rootDir);
  const files = collectSourceFiles(rootDir);
  const compatImportPath = options.compatImportPath ?? DEFAULT_COMPAT_IMPORT_PATH;
  const annotateTodos = options.annotateTodos ?? false;

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
    const fileResult = codemodFile(file, source, compatImportPath, annotateTodos);

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
        dynamicImportEdits.push({
          start: node.getStart(sourceFile),
          end: node.getEnd(),
          text: buildCompatDynamicImportExpression(compatImportPath, spec.compatSymbol),
        });
        rewrittenKinds.push(spec.kind);
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

  const compatSymbols = Array.from(requiredCompatSymbols).sort();
  const compatImportResult = ensureCompatNamedImports(
    file,
    transformed,
    compatSymbols,
    compatImportPath,
  );
  transformed = compatImportResult.nextSource;

  return {
    nextSource: transformed,
    rewrittenConstructors: constructorEdits.length,
    rewrittenDynamicImports: dynamicImportEdits.length,
    rewrittenKinds,
    addedCompatImport: compatImportResult.changed,
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
    "map-image-layer": { total: 0, autoMigrated: 0, manual: 0 },
    map: { total: 0, autoMigrated: 0, manual: 0 },
    "map-view": { total: 0, autoMigrated: 0, manual: 0 },
    "scene-view": { total: 0, autoMigrated: 0, manual: 0 },
    "web-map": { total: 0, autoMigrated: 0, manual: 0 },
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
    case "map-image-layer":
      return isSafeMapImageLayerCompatCall(node);
    case "map":
      return isSafeMapCompatCall(node);
    case "map-view":
      return isSafeMapViewCompatCall(node);
    case "scene-view":
      return isSafeSceneViewCompatCall(node);
    case "web-map":
      return isSafeWebMapCompatCall(node);
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
