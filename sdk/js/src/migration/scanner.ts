import fs from "node:fs";
import path from "node:path";

const SOURCE_EXTENSIONS = new Set([".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"]);
const SKIP_DIRS = new Set(["node_modules", "dist", ".git"]);

export interface ArcGisImportHit {
  file: string;
  modulePath: string;
  importClause: string;
  symbols: string[];
}

export interface ArcGisScanReport {
  rootDir: string;
  filesScanned: number;
  filesWithArcGisImports: number;
  imports: ArcGisImportHit[];
  symbolUsageCounts: Record<string, number>;
  flags: string[];
}

export function scanArcGisUsage(rootDir: string): ArcGisScanReport {
  const files = collectSourceFiles(rootDir);
  const imports: ArcGisImportHit[] = [];
  const flags = new Set<string>();
  const symbolUsageCounts: Record<string, number> = {};

  for (const file of files) {
    const source = fs.readFileSync(file, "utf8");
    if (source.includes("@arcgis/core/")) {
      addFileLevelFlags(source, flags);
    }

    const fileImports = findArcGisImports(source, file);
    if (fileImports.length === 0) {
      continue;
    }

    imports.push(...fileImports);

    for (const importHit of fileImports) {
      for (const symbol of importHit.symbols) {
        const usage = countIdentifierUsage(source, symbol);
        symbolUsageCounts[symbol] = (symbolUsageCounts[symbol] ?? 0) + usage;
      }
    }
  }

  return {
    rootDir: path.resolve(rootDir),
    filesScanned: files.length,
    filesWithArcGisImports: new Set(imports.map((item) => item.file)).size,
    imports,
    symbolUsageCounts,
    flags: Array.from(flags).sort(),
  };
}

export function summarizeArcGisScan(report: ArcGisScanReport): string {
  const topSymbols = Object.entries(report.symbolUsageCounts)
    .sort((a, b) => b[1] - a[1])
    .slice(0, 10)
    .map(([symbol, count]) => `${symbol}:${count}`)
    .join(", ");

  const flagText = report.flags.length > 0 ? report.flags.join(", ") : "none";
  return [
    `filesScanned=${report.filesScanned}`,
    `filesWithArcGisImports=${report.filesWithArcGisImports}`,
    `importCount=${report.imports.length}`,
    `topSymbols=[${topSymbols}]`,
    `flags=[${flagText}]`,
  ].join(" ");
}

function collectSourceFiles(rootDir: string): string[] {
  const absoluteRoot = path.resolve(rootDir);
  const queue = [absoluteRoot];
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

function findArcGisImports(source: string, file: string): ArcGisImportHit[] {
  const hits: ArcGisImportHit[] = [];
  const importRegex = /import\s+([^;]+?)\s+from\s+["'](@arcgis\/core\/[^"']+)["'];?/g;
  let importMatch: RegExpExecArray | null = importRegex.exec(source);
  while (importMatch !== null) {
    const importClause = importMatch[1].trim();
    const modulePath = importMatch[2];
    hits.push({
      file,
      modulePath,
      importClause,
      symbols: extractImportedSymbols(importClause),
    });
    importMatch = importRegex.exec(source);
  }

  const sideEffectImportRegex = /import\s+["'](@arcgis\/core\/[^"']+)["'];?/g;
  let sideEffectImportMatch: RegExpExecArray | null = sideEffectImportRegex.exec(source);
  while (sideEffectImportMatch !== null) {
    hits.push({
      file,
      modulePath: sideEffectImportMatch[1],
      importClause: "side-effect-import",
      symbols: [],
    });
    sideEffectImportMatch = sideEffectImportRegex.exec(source);
  }

  const requireRegex = /require\(["'](@arcgis\/core\/[^"']+)["']\)/g;
  let requireMatch: RegExpExecArray | null = requireRegex.exec(source);
  while (requireMatch !== null) {
    hits.push({
      file,
      modulePath: requireMatch[1],
      importClause: "require(...)",
      symbols: [],
    });
    requireMatch = requireRegex.exec(source);
  }

  const dynamicImportRegex = /import\(\s*["'](@arcgis\/core\/[^"']+)["']\s*\)/g;
  let dynamicImportMatch: RegExpExecArray | null = dynamicImportRegex.exec(source);
  while (dynamicImportMatch !== null) {
    hits.push({
      file,
      modulePath: dynamicImportMatch[1],
      importClause: "import(...)",
      symbols: [],
    });
    dynamicImportMatch = dynamicImportRegex.exec(source);
  }

  return hits;
}

function extractImportedSymbols(importClause: string): string[] {
  const symbols: string[] = [];

  const defaultImportMatch = importClause.match(/^([A-Za-z_$][A-Za-z0-9_$]*)/);
  if (defaultImportMatch) {
    symbols.push(defaultImportMatch[1]);
  }

  const namedImportMatch = importClause.match(/\{([^}]+)\}/);
  if (namedImportMatch) {
    for (const part of namedImportMatch[1].split(",")) {
      const token = part.trim();
      if (!token) {
        continue;
      }
      const aliasMatch = token.match(/\bas\s+([A-Za-z_$][A-Za-z0-9_$]*)$/);
      if (aliasMatch) {
        symbols.push(aliasMatch[1]);
      } else {
        const symbolMatch = token.match(/^([A-Za-z_$][A-Za-z0-9_$]*)$/);
        if (symbolMatch) {
          symbols.push(symbolMatch[1]);
        }
      }
    }
  }

  return Array.from(new Set(symbols));
}

function countIdentifierUsage(source: string, symbol: string): number {
  const escaped = symbol.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const regex = new RegExp(`\\b${escaped}\\b`, "g");
  const matches = source.match(regex);
  if (!matches) {
    return 0;
  }

  // Subtract one usage that usually appears in the import declaration itself.
  return Math.max(0, matches.length - 1);
}

function addFileLevelFlags(source: string, flags: Set<string>): void {
  if (/SceneView\b/.test(source) || /WebScene\b/.test(source)) {
    flags.add("scene-3d-detected");
  }
  if (/WebMap\b/.test(source)) {
    flags.add("webmap-detected");
  }
  if (/import\(\s*["']@arcgis\/core\//.test(source)) {
    flags.add("dynamic-import-detected");
  }
  if (/Sketch|Editor|Track|Directions|RouteLayer/.test(source)) {
    flags.add("advanced-widget-or-networking-detected");
  }
}
