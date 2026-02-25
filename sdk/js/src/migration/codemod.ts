import fs from "node:fs";
import path from "node:path";
import ts from "typescript";

const SOURCE_EXTENSIONS = new Set([".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"]);
const SKIP_DIRS = new Set(["node_modules", "dist", ".git"]);

const FEATURE_LAYER_MODULES = new Set([
  "@arcgis/core/layers/FeatureLayer",
  "@arcgis/core/layers/FeatureLayer.js",
]);

const DEFAULT_COMPAT_IMPORT_PATH = "@honua/sdk-esri-compat";

interface TextEdit {
  start: number;
  end: number;
  text: string;
}

interface FeatureLayerImport {
  modulePath: string;
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
  totalFeatureLayerCallSites: number;
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
    totalFeatureLayerCallSites: 0,
    autoMigratedCallSites: 0,
    manualCallSites: 0,
  };
  const fileResults: CodemodFileResult[] = [];
  const manualTodos: MigrationTodo[] = [];

  for (const file of files) {
    const source = fs.readFileSync(file, "utf8");
    const fileResult = codemodFile(file, source, compatImportPath);
    metrics.totalFeatureLayerCallSites +=
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

function codemodFile(file: string, source: string, compatImportPath: string): {
  nextSource: string;
  rewrittenConstructors: number;
  addedCompatImport: boolean;
  removedArcGisImports: number;
  manualTodos: MigrationTodo[];
} {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  const featureLayerImports = collectFeatureLayerImports(sourceFile);
  if (featureLayerImports.length === 0) {
    return {
      nextSource: source,
      rewrittenConstructors: 0,
      addedCompatImport: false,
      removedArcGisImports: 0,
      manualTodos: [],
    };
  }

  const importNames = new Set(featureLayerImports.map((item) => item.localName));
  const constructorEdits: TextEdit[] = [];
  const manualTodos: MigrationTodo[] = [];

  walk(sourceFile, (node) => {
    if (!ts.isNewExpression(node)) {
      return;
    }
    if (!ts.isIdentifier(node.expression)) {
      return;
    }
    if (!importNames.has(node.expression.text)) {
      return;
    }

    const safeCheck = isSafeFeatureLayerCompatCall(node);
    if (safeCheck.ok) {
      constructorEdits.push({
        start: node.expression.getStart(sourceFile),
        end: node.expression.getEnd(),
        text: "FeatureLayerCompat",
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
  const removedArcGisImports = removeUnusedArcGisFeatureLayerImports(file, transformed);
  transformed = removedArcGisImports.nextSource;
  const addedCompatImport = ensureCompatImport(
    file,
    transformed,
    "FeatureLayerCompat",
    compatImportPath,
  );
  transformed = addedCompatImport.nextSource;

  return {
    nextSource: transformed,
    rewrittenConstructors: constructorEdits.length,
    addedCompatImport: addedCompatImport.added,
    removedArcGisImports: removedArcGisImports.removedCount,
    manualTodos: manualTodos.sort(compareTodos),
  };
}

function ensureCompatImport(
  file: string,
  source: string,
  symbol: string,
  importPath: string,
): { nextSource: string; added: boolean } {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement)) {
      continue;
    }
    if (!ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (statement.moduleSpecifier.text !== importPath) {
      continue;
    }
    const namedBindings = statement.importClause?.namedBindings;
    if (namedBindings && ts.isNamedImports(namedBindings)) {
      for (const element of namedBindings.elements) {
        if (element.name.text === symbol) {
          return { nextSource: source, added: false };
        }
      }
    }
  }

  const importLine = `import { ${symbol} } from "${importPath}";`;
  const insertionIndex = findImportInsertionIndex(sourceFile);
  const prefix = source.slice(0, insertionIndex);
  const suffix = source.slice(insertionIndex);

  const needsLeadingNewline = prefix.length > 0 && !prefix.endsWith("\n");
  const leading = needsLeadingNewline ? "\n" : "";
  const needsTrailingNewline =
    suffix.length > 0 && !suffix.startsWith("\n") && !importLine.endsWith("\n");
  const trailing = needsTrailingNewline ? "\n" : "";

  const nextSource = `${prefix}${leading}${importLine}${trailing}${suffix}`;
  return { nextSource, added: true };
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

function removeUnusedArcGisFeatureLayerImports(
  file: string,
  source: string,
): { nextSource: string; removedCount: number } {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  const imports = collectFeatureLayerImports(sourceFile);
  const removals: TextEdit[] = [];

  for (const importNode of imports) {
    if (importNode.hasNamedBindings) {
      continue;
    }
    const references = countIdentifierUsagesExcludingImports(sourceFile, importNode.localName);
    if (references > 0) {
      continue;
    }

    const bounds = expandToFullLine(source, importNode.start, importNode.end);
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

function collectFeatureLayerImports(sourceFile: ts.SourceFile): FeatureLayerImport[] {
  const imports: FeatureLayerImport[] = [];

  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement)) {
      continue;
    }
    if (!ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (!FEATURE_LAYER_MODULES.has(statement.moduleSpecifier.text)) {
      continue;
    }

    const importClause = statement.importClause;
    if (!importClause?.name) {
      continue;
    }

    const hasNamedBindings = importClause.namedBindings !== undefined;
    imports.push({
      modulePath: statement.moduleSpecifier.text,
      localName: importClause.name.text,
      start: statement.getStart(sourceFile),
      end: statement.getEnd(),
      hasNamedBindings,
    });
  }

  return imports;
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

function applyTextEdits(source: string, edits: TextEdit[]): string {
  const sorted = edits
    .slice()
    .sort((a, b) => (a.start === b.start ? b.end - a.end : b.start - a.start));

  let nextSource = source;
  for (const edit of sorted) {
    nextSource = `${nextSource.slice(0, edit.start)}${edit.text}${nextSource.slice(edit.end)}`;
  }
  return nextSource;
}

function isSafeFeatureLayerCompatCall(node: ts.NewExpression): { ok: true } | { ok: false; reason: string } {
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
    if (!ts.isPropertyAssignment(property)) {
      return {
        ok: false,
        reason: "FeatureLayer options contain spread/shorthand/method syntax; requires manual migration.",
      };
    }

    if (!isUrlPropertyName(property.name)) {
      return {
        ok: false,
        reason: "FeatureLayer options include non-url properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isUrlPropertyName(name: ts.PropertyName): boolean {
  if (ts.isIdentifier(name)) {
    return name.text === "url";
  }
  if (ts.isStringLiteral(name) || ts.isNoSubstitutionTemplateLiteral(name)) {
    return name.text === "url";
  }
  return false;
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
