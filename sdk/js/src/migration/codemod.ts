import fs from "node:fs";
import path from "node:path";
import ts from "typescript";

const SOURCE_EXTENSIONS = new Set([".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"]);
const SKIP_DIRS = new Set(["node_modules", "dist", ".git"]);
const DEFAULT_COMPAT_IMPORT_PATH = "@honua/sdk-esri-compat";

type ConstructorKind = "feature-layer" | "map" | "map-view";

interface ConstructorRewriteSpec {
  kind: ConstructorKind;
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
];

const MODULE_TO_SPEC = buildModuleToSpecLookup(REWRITE_SPECS);

interface TextEdit {
  start: number;
  end: number;
  text: string;
}

interface ArcGisImportBinding {
  kind: ConstructorKind;
  localName: string;
  start: number;
  end: number;
  hasNamedBindings: boolean;
}

export interface MigrationTodo {
  file: string;
  line: number;
  column: number;
  reason: string;
}

export interface CodemodMetrics {
  totalCodemodScopedCallSites: number;
  autoMigratedCallSites: number;
  manualCallSites: number;
}

export interface CodemodFileResult {
  file: string;
  rewrittenConstructors: number;
  addedCompatImport: boolean;
  removedArcGisImports: number;
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
}

export function runEsriCompatCodemod(options: EsriCompatCodemodOptions): EsriCompatCodemodResult {
  const rootDir = path.resolve(options.rootDir);
  const files = collectSourceFiles(rootDir);
  const compatImportPath = options.compatImportPath ?? DEFAULT_COMPAT_IMPORT_PATH;

  const metrics: CodemodMetrics = {
    totalCodemodScopedCallSites: 0,
    autoMigratedCallSites: 0,
    manualCallSites: 0,
  };
  const fileResults: CodemodFileResult[] = [];
  const manualTodos: MigrationTodo[] = [];

  for (const file of files) {
    const source = fs.readFileSync(file, "utf8");
    const fileResult = codemodFile(file, source, compatImportPath);

    metrics.totalCodemodScopedCallSites +=
      fileResult.rewrittenConstructors + fileResult.manualTodos.length;
    metrics.autoMigratedCallSites += fileResult.rewrittenConstructors;
    metrics.manualCallSites += fileResult.manualTodos.length;
    manualTodos.push(...fileResult.manualTodos);

    const hasChanges =
      fileResult.rewrittenConstructors > 0 ||
      fileResult.addedCompatImport ||
      fileResult.removedArcGisImports > 0;
    if (hasChanges) {
      if (options.write) {
        fs.writeFileSync(file, fileResult.nextSource, "utf8");
      }
      fileResults.push({
        file,
        rewrittenConstructors: fileResult.rewrittenConstructors,
        addedCompatImport: fileResult.addedCompatImport,
        removedArcGisImports: fileResult.removedArcGisImports,
        manualTodos: fileResult.manualTodos,
      });
    } else if (fileResult.manualTodos.length > 0) {
      fileResults.push({
        file,
        rewrittenConstructors: 0,
        addedCompatImport: false,
        removedArcGisImports: 0,
        manualTodos: fileResult.manualTodos,
      });
    }
  }

  return {
    rootDir,
    filesScanned: files.length,
    filesChanged: fileResults.filter(
      (item) => item.rewrittenConstructors > 0 || item.addedCompatImport || item.removedArcGisImports > 0,
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
): {
  nextSource: string;
  rewrittenConstructors: number;
  addedCompatImport: boolean;
  removedArcGisImports: number;
  manualTodos: MigrationTodo[];
} {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  const imports = collectSupportedImports(sourceFile);
  if (imports.length === 0) {
    return {
      nextSource: source,
      rewrittenConstructors: 0,
      addedCompatImport: false,
      removedArcGisImports: 0,
      manualTodos: [],
    };
  }

  const importsByLocalName = new Map<string, ArcGisImportBinding>();
  for (const importBinding of imports) {
    if (!importsByLocalName.has(importBinding.localName)) {
      importsByLocalName.set(importBinding.localName, importBinding);
    }
  }

  const constructorEdits: TextEdit[] = [];
  const manualTodos: MigrationTodo[] = [];
  const requiredCompatSymbols = new Set<string>();

  walk(sourceFile, (node) => {
    if (!ts.isNewExpression(node) || !ts.isIdentifier(node.expression)) {
      return;
    }

    const importBinding = importsByLocalName.get(node.expression.text);
    if (!importBinding) {
      return;
    }

    const safeCheck = isSafeConstructorCall(importBinding.kind, node);
    if (safeCheck.ok) {
      const spec = specForKind(importBinding.kind);
      requiredCompatSymbols.add(spec.compatSymbol);
      constructorEdits.push({
        start: node.expression.getStart(sourceFile),
        end: node.expression.getEnd(),
        text: spec.compatSymbol,
      });
      return;
    }

    const location = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile));
    manualTodos.push({
      file,
      line: location.line + 1,
      column: location.character + 1,
      reason: safeCheck.reason,
    });
  });

  if (constructorEdits.length === 0) {
    return {
      nextSource: source,
      rewrittenConstructors: 0,
      addedCompatImport: false,
      removedArcGisImports: 0,
      manualTodos: manualTodos.sort(compareTodos),
    };
  }

  let transformed = applyTextEdits(source, constructorEdits);
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
    addedCompatImport: compatImportResult.changed,
    removedArcGisImports: removedArcGisImports.removedCount,
    manualTodos: manualTodos.sort(compareTodos),
  };
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
    if (!importClause?.name) {
      continue;
    }

    result.push({
      kind: spec.kind,
      localName: importClause.name.text,
      start: statement.getStart(sourceFile),
      end: statement.getEnd(),
      hasNamedBindings: importClause.namedBindings !== undefined,
    });
  }

  return result;
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

function removeUnusedArcGisImports(
  file: string,
  source: string,
): { nextSource: string; removedCount: number } {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  const imports = collectSupportedImports(sourceFile);
  const removals: TextEdit[] = [];

  for (const importBinding of imports) {
    if (importBinding.hasNamedBindings) {
      continue;
    }
    const references = countIdentifierUsagesExcludingImports(sourceFile, importBinding.localName);
    if (references > 0) {
      continue;
    }

    const bounds = expandToFullLine(source, importBinding.start, importBinding.end);
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
  kind: ConstructorKind,
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  switch (kind) {
    case "feature-layer":
      return isSafeFeatureLayerCompatCall(node);
    case "map":
      return isSafeMapCompatCall(node);
    case "map-view":
      return isSafeMapViewCompatCall(node);
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

  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "FeatureLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    if (getObjectPropertyName(property) !== "url") {
      return {
        ok: false,
        reason: "FeatureLayer options include non-url properties; requires manual migration.",
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

function specForKind(kind: ConstructorKind): ConstructorRewriteSpec {
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
  return a.reason.localeCompare(b.reason);
}
