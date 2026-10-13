// Same-run transport only. The shell contract owns payload and identity validation.
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {mkdtemp, readFile, writeFile, rm, stat} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const exec = promisify(execFile);
const properties = 'TreatWarningsAsErrors=true;RunAnalyzers=true;UseSharedCompilation=false';
const maxBytes = 335544320;
export async function run(options, deps = {}) {
  const root = options.root ?? process.env.GITHUB_WORKSPACE;
  const env = {...process.env, ...deps.env};
  const runner = deps.runner ?? (async (file, args, extra = {}) => exec(file, args, {
    cwd: root, env: {...env, ...extra}, timeout: 120000, maxBuffer: 4 * 1024 * 1024
  }));
  if (!['publish', 'consume'].includes(options.mode)) throw Error('Invalid mode');
  if (!['true', 'false'].includes(options.enabled ?? 'true')) throw Error('Invalid enabled boolean');
  if ((options.configuration ?? 'Release') !== 'Release') throw Error('Only Release artifacts are supported');
  if ((options.buildProperties ?? properties) !== properties) throw Error('Unexpected build property identity');
  const projects = JSON.parse(await readFile(options.projectsFile, 'utf8'));
  if (!Array.isArray(projects) || projects.length >= 100 || new Set(projects).size !== projects.length ||
      projects.some(p => typeof p !== 'string' || !/^tests\/dotnet\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\.csproj$/.test(p))) {
    throw Error('Invalid projects-file plan (unique project array, fewer than 100 projects required)');
  }
  const resultDir = await mkdtemp(path.join(env.RUNNER_TEMP ?? tmpdir(), 'managed-test-receipt-'));
  const restoredFile = path.join(resultDir, 'restored-projects.json');
  const receiptFile = path.join(resultDir, 'receipt.json');
  const restored = [], receipts = [];
  await writeFile(restoredFile, '[]\n');
  const save = () => writeFile(receiptFile, JSON.stringify({sourceSha, workflowRunId: env.GITHUB_RUN_ID,
    producerAttempt: attempt, projects: receipts}, null, 2) + '\n');
  let sourceSha = null;
  const attempt = options.producerAttempt || env.GITHUB_RUN_ATTEMPT;
  if ((options.enabled ?? 'true') === 'false') {
    receipts.push(...projects.map(project => ({project, status: 'disabled'})));
    await save();
    return {restoredProjectsFile: restoredFile, receiptFile, producerAttempt: attempt};
  }
  if (!/^[1-9][0-9]*$/.test(env.GITHUB_RUN_ID ?? '') || !/^[1-9][0-9]*$/.test(attempt ?? '')) throw Error('Invalid run/attempt identity');
  if (options.mode === 'publish' && attempt !== env.GITHUB_RUN_ATTEMPT) throw Error('Publish must use the current workflow attempt');
  const registry = JSON.parse(await readFile(path.join(root, '.github/server-test-artifact-projects.json'), 'utf8'));
  const entries = projects.map(project => {
    const found = registry.projects.filter(item => item.csproj === project);
    if (found.length !== 1 || !/^[a-z0-9-]+$/.test(found[0].artifact_suffix)) throw Error(`Unregistered or ambiguous project: ${project}`);
    return {project, suffix: found[0].artifact_suffix};
  });
  if (new Set(entries.map(e => e.suffix)).size !== entries.length) throw Error('Duplicate artifact suffix');
  sourceSha = (await runner('git', ['rev-parse', 'HEAD'])).stdout.trim();
  if (!/^[a-f0-9]{40}$/.test(sourceSha)) throw Error('Invalid checkout source identity');
  const identity = {HONUA_SERVER_TEST_ARTIFACT_WORKFLOW_RUN_ID: env.GITHUB_RUN_ID,
    HONUA_SERVER_TEST_ARTIFACT_BUILD_PROPERTIES: properties, HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION: 'Release'};
  const artifact = deps.artifact ?? new (await import('@actions/artifact')).DefaultArtifactClient();
  try {
    for (const {project, suffix} of entries) {
      const temp = await mkdtemp(path.join(env.RUNNER_TEMP ?? tmpdir(), 'managed-test-payload-'));
      const record = {project, sourceSha, name: `managed-tests-${attempt}-${suffix}`, status: 'unrestored'};
      let packaging = options.mode === 'publish';
      try {
        const manifest = path.join(temp, `server-test-binaries-${suffix}.manifest.json`);
        const archive = path.join(temp, `server-test-binaries-${suffix}.tar.gz`);
        if (options.mode === 'publish') {
          await runner('bash', [path.join(root, 'scripts/ci/package-server-test-binaries.sh'), '--project', project,
            '--output', temp, '--source-sha', sourceSha], identity);
          const metadata = JSON.parse(await readFile(manifest, 'utf8'));
          if ((await stat(archive)).size > maxBytes) throw Error('Packaged archive exceeds limit');
          record.archiveSha256 = metadata.archive_sha256;
          packaging = false;
          const uploaded = await artifact.uploadArtifact(record.name, [manifest, archive], temp,
            {retentionDays: 1, compressionLevel: 0});
          if (!uploaded.id) throw Error('Upload did not return an artifact id');
          record.status = 'published'; record.artifactId = uploaded.id;
        } else {
          const {artifact: found} = await artifact.getArtifact(record.name);
          if (!found?.id) throw Error('Artifact missing');
          // Transport zip includes the tar and a small manifest; reject before download.
          if (!Number.isFinite(found.size) || found.size > maxBytes + 1048576) throw Error('Artifact transport size exceeds limit');
          await artifact.downloadArtifact(found.id, {path: temp});
          // Truncated/missing JSON is transport corruption, not a valid but
          // incompatible identity. Keep that distinction before the shell gate.
          JSON.parse(await readFile(manifest, 'utf8'));
          try {
            await runner('bash', [path.join(root, 'scripts/ci/restore-server-test-binaries.sh'), '--manifest', manifest,
              '--destination', root, '--project', project, '--source-sha', sourceSha], identity);
          } catch (error) {
            if (/exact source\/project\/toolchain contract|Strict artifacts require|Strict artifacts require a valid SDK|supports Linux/.test(error.stderr ?? '')) {
              error.identityFailure = true;
            }
            throw error;
          }
          record.archiveSha256 = JSON.parse(await readFile(manifest, 'utf8')).archive_sha256;
          restored.push(project);
          await writeFile(restoredFile, JSON.stringify(restored) + '\n');
          record.status = 'restored'; record.artifactId = found.id;
        }
      } catch (error) {
        const timedOut = error.code === 'ETIMEDOUT' || error.code === 124 || (error.killed && error.signal === 'SIGTERM');
        const packageBudget = packaging && (timedOut ||
          /Staged payload.*exceeds|Archive.*exceeds|Packaging.*exceeds|Packaged archive exceeds limit/.test(
            `${error.stderr ?? ''} ${error.message ?? ''}`));
        record.reason = error.identityFailure ? 'identity-mismatch' : packageBudget ? 'package-budget-exceeded' :
          timedOut ? 'timeout' : 'artifact-unavailable-or-invalid';
        if (error.identityFailure || (packaging && !packageBudget)) throw error;
      } finally {
        receipts.push(record);
        await rm(temp, {recursive: true, force: true});
        await save();
      }
    }
  } finally { await save(); }
  return {restoredProjectsFile: restoredFile, receiptFile, producerAttempt: attempt};
}

// SDK calls run in killable children so a timed-out request cannot continue
// writing into a cleaned temp directory or overlap the next project's transfer.
export async function execute(options) {
  const artifact = Object.fromEntries(['uploadArtifact', 'getArtifact', 'downloadArtifact'].map(method =>
    [method, async (...args) => {
      const response = await exec(process.execPath,
        [fileURLToPath(import.meta.url), '--sdk', method, JSON.stringify(args)],
        {env: process.env, timeout: 180000, maxBuffer: 8 * 1024 * 1024});
      return JSON.parse(response.stdout.split('\n').findLast(line => line.startsWith('{')));
    }]));
  return run(options, {artifact});
}
if (process.argv[2] === '--sdk') {
  const {DefaultArtifactClient} = await import('@actions/artifact');
  try {
    const result = await new DefaultArtifactClient()[process.argv[3]](...JSON.parse(process.argv[4]));
    console.log(JSON.stringify(result));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
