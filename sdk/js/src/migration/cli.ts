#!/usr/bin/env node

import { scanArcGisUsage, summarizeArcGisScan } from "./scanner.js";

const target = process.argv[2] ?? process.cwd();
const report = scanArcGisUsage(target);
const summary = summarizeArcGisScan(report);

process.stdout.write(`${summary}\n`);
process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
