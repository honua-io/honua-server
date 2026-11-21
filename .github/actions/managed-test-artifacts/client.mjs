// Same-run transport only. The shell contract owns payload and identity validation.
import {execFile, spawn} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import {promisify} from 'node:util';
import {copyFile, link, lstat, mkdir, mkdtemp, readFile, rename, writeFile, rm, stat, readdir} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const exec = promisify(execFile);
const properties = 'TreatWarningsAsErrors=true;RunAnalyzers=true;UseSharedCompilation=false';
const maxBytes = 335544320;
const maxProjects = 100;
const stagingSuffixBytes = 6;
const transportOverheadBytes = 1048576;
const sha256Pattern = /^[a-f0-9]{64}$/;
const projectPattern = /^tests\/dotnet\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\.csproj$/;
const identityErrorPattern = /exact source\/project\/toolchain contract|Strict artifacts require|Strict artifacts require a valid SDK|supports Linux/;

async function resolveLocalStore(env) {
  const configured = (env.HONUA_MANAGED_ARTIFACT_LOCAL_STORE ?? '').trim();
  if (!configured) return {configured: false, dir: null};
  if (!path.isAbsolute(configured)) return {configured: true, dir: null};
  try {
    const info = await stat(configured);
    return {configured: true, dir: info.isDirectory() ? configured : null};
  } catch {
    return {configured: true, dir: null};
  }
}

async function hasRegularFileSize(filePath, expectedSize) {
  try {
    const info = await lstat(filePath);
    return info.isFile() && info.size === expectedSize;
  } catch {
    return false;
  }
}

async function atomicPlace(src, dst, reuseExistingSize = null) {
  if (reuseExistingSize !== null && await hasRegularFileSize(dst, reuseExistingSize)) return;
  await mkdir(path.dirname(dst), {recursive: true});
  const staging = `${dst}.tmp.${process.pid}.${randomBytes(stagingSuffixBytes).toString('hex')}`;
  try {
    await copyFile(src, staging);
    await rename(staging, dst);
  } catch (error) {
    await rm(staging, {force: true});
    throw error;
  }
}

async function stageLocalArtifact(storeDir, {runId, attempt, name, archiveSha256, manifest, archive, archiveSize}) {
  if (!sha256Pattern.test(archiveSha256 ?? '')) throw Error('Invalid archive SHA-256');
  const blobPath = path.join(storeDir, 'blobs', `${archiveSha256}.tar.gz`);
  const runManifestPath = path.join(storeDir, 'runs', runId, attempt, `${name}.manifest.json`);
  await atomicPlace(archive, blobPath, archiveSize);
  await atomicPlace(manifest, runManifestPath);
}

async function materializeLocalArtifact(storeDir, {runId, attempt, name, manifest, archive}) {
  const runManifestPath = path.join(storeDir, 'runs', runId, attempt, `${name}.manifest.json`);
  if (!(await lstat(runManifestPath)).isFile()) throw Error('Local manifest is not a regular file');
  const manifestText = await readFile(runManifestPath, 'utf8');
  const metadata = JSON.parse(manifestText);
  if (typeof metadata.archive_sha256 !== 'string' || !sha256Pattern.test(metadata.archive_sha256)) {
    throw Error('Invalid local manifest archive SHA-256');
  }
  const blobPath = path.join(storeDir, 'blobs', `${metadata.archive_sha256}.tar.gz`);
  const blobStat = await lstat(blobPath);
  if (!blobStat.isFile() || !Number.isFinite(blobStat.size) || blobStat.size > maxBytes) {
    throw Error('Local artifact archive is invalid or exceeds limit');
  }
  await writeFile(manifest, manifestText, 'utf8');
  try {
    await link(blobPath, archive);
  } catch {
    await copyFile(blobPath, archive);
  }
  return metadata;
}

async function loadLocalOnlyProjects(filePath) {
  if (!filePath || !String(filePath).trim()) return new Set();
  const parsed = JSON.parse(await readFile(filePath, 'utf8'));
  if (!Array.isArray(parsed) || parsed.length >= maxProjects ||
      parsed.some(p => typeof p !== 'string' || !projectPattern.test(p))) {
    throw Error('Invalid local-only-projects-file plan');
  }
  return new Set(parsed);
}

async function restorePayload({runner, root, manifest, project, sourceSha, identity}) {
  try {
    await runner('bash', [path.join(root, 'scripts/ci/restore-server-test-binaries.sh'), '--manifest', manifest,
      '--destination', root, '--project', project, '--source-sha', sourceSha], identity);
  } catch (error) {
    if (identityErrorPattern.test(error.stderr ?? '')) error.identityFailure = true;
    throw error;
  }
}

async function packageProject(ctx, item) {
  const {root, sourceSha, identity, runner} = ctx;
  const {project, temp, manifest, archive} = item;
  await runner('bash', [path.join(root, 'scripts/ci/package-server-test-binaries.sh'), '--project', project,
    '--output', temp, '--source-sha', sourceSha], identity);
  const metadata = JSON.parse(await readFile(manifest, 'utf8'));
  const archiveSize = (await stat(archive)).size;
  if (archiveSize > maxBytes) throw Error('Packaged archive exceeds limit');
  return {archiveSha256: metadata.archive_sha256, archiveSize};
}

async function deliverPublishedArtifact(ctx, item, packaged) {
  const {env, attempt, transfer, localStore, localOnlyProjects} = ctx;
  const {project, temp, manifest, archive, record} = item;
  let stagedLocal = false;
  if (localStore.dir) {
    try {
      await stageLocalArtifact(localStore.dir, {
        runId: env.GITHUB_RUN_ID, attempt, name: record.name,
        archiveSha256: packaged.archiveSha256, manifest, archive, archiveSize: packaged.archiveSize
      });
      stagedLocal = true;
    } catch { record.localStoreFallback = 'stage-failed'; }
  } else if (localStore.configured) {
    record.localStoreFallback = 'store-unavailable';
  }
  if (stagedLocal && localOnlyProjects.has(project)) {
    return {status: 'published', artifactId: null, transport: 'local'};
  }
  const uploaded = await transfer('uploadArtifact', record.name, [manifest, archive], temp,
    {retentionDays: 1, compressionLevel: 0});
  if (!uploaded.id) throw Error('Upload did not return an artifact id');
  return {status: 'published', artifactId: uploaded.id, transport: stagedLocal ? 'local-and-remote' : 'remote'};
}

async function tryLocalConsume(ctx, item) {
  const {root, env, attempt, sourceSha, identity, runner, localStore, restored, restoredFile} = ctx;
  const {project, manifest, archive, record} = item;
  if (!localStore.dir) {
    if (localStore.configured) record.localStoreFallback = 'store-unavailable';
    return false;
  }
  try {
    const metadata = await materializeLocalArtifact(localStore.dir, {
      runId: env.GITHUB_RUN_ID, attempt, name: record.name, manifest, archive
    });
    await restorePayload({runner, root, manifest, project, sourceSha, identity});
    record.archiveSha256 = metadata.archive_sha256;
    restored.push(project);
    await writeFile(restoredFile, JSON.stringify(restored) + '\n');
    record.status = 'restored'; record.artifactId = null; record.transport = 'local';
    return true;
  } catch (error) {
    if (error.identityFailure || error.groupNotQuiescent || error.code === 'TRANSPORT_BUDGET') throw error;
    record.localStoreFallback = 'miss-or-invalid';
    await rm(manifest, {force: true});
    await rm(archive, {force: true});
    return false;
  }
}

async function consumeProject(ctx, item) {
  if (await tryLocalConsume(ctx, item)) return;
  const {root, sourceSha, identity, runner, transfer, restored, restoredFile} = ctx;
  const {project, temp, manifest, record} = item;
  const {artifact: found} = await transfer('getArtifact', record.name);
  if (!found?.id) throw Error('Artifact missing');
  // Transport zip includes the tar and a small manifest; reject before download.
  if (!Number.isFinite(found.size) || found.size > maxBytes + transportOverheadBytes) throw Error('Artifact transport size exceeds limit');
  await transfer('downloadArtifact', found.id, {path: temp});
  // Truncated/missing JSON is transport corruption, not a valid but
  // incompatible identity. Keep that distinction before the shell gate.
  const metadata = JSON.parse(await readFile(manifest, 'utf8'));
  await restorePayload({runner, root, manifest, project, sourceSha, identity});
  record.archiveSha256 = metadata.archive_sha256;
  restored.push(project);
  await writeFile(restoredFile, JSON.stringify(restored) + '\n');
  record.status = 'restored'; record.artifactId = found.id; record.transport = 'remote';
}
// Each shell owns a fresh Linux process group. A timed-out group gets a
// rollback grace period, then KILL; cleanup waits until no live member remains.
async function liveGroup(group) {
  for (const name of await readdir('/proc')) {
    if (!/^\d+$/.test(name)) continue;
    try {
      const text = await readFile(`/proc/${name}/stat`, 'utf8');
      const fields = text.slice(text.lastIndexOf(')') + 2).split(' ');
      if (Number(fields[2]) === group && !['Z', 'X'].includes(fields[0])) return true;
    } catch (error) { if (!['ENOENT', 'ESRCH'].includes(error.code)) throw error; }
  }
  return false;
}
export async function boundedRunner(file, args, {cwd, env, timeout}) {
  if (!Number.isFinite(timeout) || timeout <= 0) throw Error('Invalid subprocess timeout');
  const child = spawn(file, args, {cwd, env, detached:true, stdio:['ignore','pipe','pipe']});
  let stdout = '', stderr = '', stopping = false;
  const closed = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('close', (code, signal) => resolve({code, signal}));
  });
  const killGroup = signal => {
    try { process.kill(-child.pid, signal); }
    catch (error) { if (error.code !== 'ESRCH') throw error; }
  };
  let stopPromise, resolveStopped, rejectStopped;
  const stopped = new Promise((resolve, reject) => { resolveStopped = resolve; rejectStopped = reject; });
  const stop = () => {
    if (stopping) return;
    stopping = true;
    stopPromise = (async () => {
      killGroup('SIGTERM');
      const grace = Date.now() + 3000;
      while (await liveGroup(child.pid)) {
        if (Date.now() >= grace) { killGroup('SIGKILL'); break; }
        await new Promise(resolve => setTimeout(resolve, 40));
      }
      // Zombies cannot write; /proc inspection distinguishes them from live
      // descendants when the runner's init has not reaped an orphan yet.
      const reapDeadline = Date.now() + 2000;
      while (await liveGroup(child.pid)) {
        if (Date.now() >= reapDeadline) throw Object.assign(Error('Owned process group did not terminate'),
          {groupNotQuiescent:true});
        await new Promise(resolve => setTimeout(resolve, 20));
      }
    })();
    stopPromise = stopPromise.catch(error => {
      killGroup('SIGKILL');
      error.groupNotQuiescent = true;
      throw error;
    });
    stopPromise.then(() => resolveStopped({code:null, signal:'SIGKILL'}), rejectStopped);
  };
  for (const [stream, field] of [[child.stdout,'stdout'],[child.stderr,'stderr']]) {
    stream.on('data', data => {
      if (stopping) return;
      if (field === 'stdout') stdout += data; else stderr += data;
      if (Buffer.byteLength(stdout) + Buffer.byteLength(stderr) > 4 * 1024 * 1024) stop();
    });
  }
  const timer = setTimeout(stop, timeout);
  let result;
  try {
    result = await Promise.race([closed, stopped]);
    if (stopPromise) await stopPromise;
  } finally { clearTimeout(timer); }
  if (stopping || result.code !== 0) throw Object.assign(Error(stopping ? 'Subprocess deadline exceeded' : 'Subprocess failed'),
    {code:stopping ? 'ETIMEDOUT' : result.code, signal:result.signal, killed:stopping, stdout, stderr});
  return {stdout, stderr};
}

export async function run(options, deps = {}) {
  const root = options.root ?? process.env.GITHUB_WORKSPACE;
  const env = {...process.env, ...deps.env};
  const now = deps.now ?? Date.now;
  const deadline = now() + 600000;
  const remaining = cap => Math.max(1, Math.min(cap, deadline - now()));
  const actualRunner = deps.runner ?? ((file, args, extra = {}, timeout) => boundedRunner(file, args, {
    cwd: root, env: {...env, ...extra}, timeout
  }));
  const runner = (file, args, extra = {}) => {
    if (now() >= deadline) throw Object.assign(Error('Transport budget exhausted'), {code:'TRANSPORT_BUDGET'});
    return actualRunner(file, args, extra, remaining(120000));
  };
  if (!['publish', 'consume'].includes(options.mode)) throw Error('Invalid mode');
  if (!['true', 'false'].includes(options.enabled ?? 'true')) throw Error('Invalid enabled boolean');
  if ((options.configuration ?? 'Release') !== 'Release') throw Error('Only Release artifacts are supported');
  if ((options.buildProperties ?? properties) !== properties) throw Error('Unexpected build property identity');
  const projects = JSON.parse(await readFile(options.projectsFile, 'utf8'));
  if (!Array.isArray(projects) || projects.length >= 100 || new Set(projects).size !== projects.length ||
      projects.some(p => typeof p !== 'string' || !projectPattern.test(p))) {
    throw Error('Invalid projects-file plan (unique project array, fewer than 100 projects required)');
  }
  const localOnlyProjects = options.mode === 'publish' ? await loadLocalOnlyProjects(options.localOnlyProjectsFile) : new Set();
  const localStore = await resolveLocalStore(env);
  const resultDir = await mkdtemp(path.join(env.RUNNER_TEMP ?? tmpdir(), 'managed-test-receipt-'));
  const restoredFile = path.join(resultDir, 'restored-projects.json');
  const receiptFile = path.join(resultDir, 'receipt.json');
  const restored = [], receipts = [];
  const reportFields = ['reason', 'budget', 'observed', 'limit', 'artifactId', 'archiveSha256', 'transport', 'localStoreFallback'];
  const report = record => {
    const payload = {kind: 'managed-test-artifact', project: record.project, status: record.status,
      sourceSha: record.sourceSha ?? sourceSha};
    for (const key of reportFields) payload[key] = record[key] ?? null;
    (deps.log ?? console.log)(JSON.stringify(payload));
  };
  await writeFile(restoredFile, '[]\n');
  const save = () => writeFile(receiptFile, JSON.stringify({sourceSha, workflowRunId: env.GITHUB_RUN_ID,
    producerAttempt: attempt, projects: receipts}, null, 2) + '\n');
  let sourceSha = null;
  const attempt = options.producerAttempt || env.GITHUB_RUN_ATTEMPT;
  if ((options.enabled ?? 'true') === 'false') {
    receipts.push(...projects.map(project => ({project, status: 'disabled'})));
    receipts.forEach(report);
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
  // Publish the highest fan-out assembly before spending the shared transfer
  // budget on other outputs. Consumers preserve their requested restore order.
  if (options.mode === 'publish') {
    const server = entries.findIndex(entry => entry.project === 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj');
    if (server > 0) entries.unshift(...entries.splice(server, 1));
  }
  sourceSha = (await runner('git', ['rev-parse', 'HEAD'])).stdout.trim();
  if (!/^[a-f0-9]{40}$/.test(sourceSha)) throw Error('Invalid checkout source identity');
  const identity = {HONUA_SERVER_TEST_ARTIFACT_WORKFLOW_RUN_ID: env.GITHUB_RUN_ID,
    HONUA_SERVER_TEST_ARTIFACT_BUILD_PROPERTIES: properties, HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION: 'Release'};
  const artifact = deps.artifact ?? new (await import('@actions/artifact')).DefaultArtifactClient();
  const transfer = async (method, ...args) => {
    if (now() >= deadline) throw Object.assign(Error('Transport budget exhausted'), {code:'TRANSPORT_BUDGET'});
    // execute() uses this deadline to kill the SDK child before cleanup.
    return deps.transfer ? deps.transfer(method, args, remaining(180000)) : artifact[method](...args);
  };
  try {
    for (const {project, suffix} of entries) {
      if (now() >= deadline) {
        const skipped = {project, sourceSha, name: `managed-tests-${attempt}-${suffix}`,
          status: 'unrestored', reason: 'transport-budget-exhausted'};
        receipts.push(skipped);
        report(skipped);
        await save();
        continue;
      }
      const temp = await mkdtemp(path.join(env.RUNNER_TEMP ?? tmpdir(), 'managed-test-payload-'));
      const record = {project, sourceSha, name: `managed-tests-${attempt}-${suffix}`, status: 'unrestored'};
      const item = {
        project, temp, record,
        manifest: path.join(temp, `server-test-binaries-${suffix}.manifest.json`),
        archive: path.join(temp, `server-test-binaries-${suffix}.tar.gz`)
      };
      let packaging = options.mode === 'publish';
      const ctx = {root, env, attempt, sourceSha, identity, runner, transfer, localStore, localOnlyProjects, restored, restoredFile};
      try {
        if (options.mode === 'publish') {
          const packaged = await packageProject(ctx, item);
          record.archiveSha256 = packaged.archiveSha256;
          packaging = false;
          Object.assign(record, await deliverPublishedArtifact(ctx, item, packaged));
        } else {
          await consumeProject(ctx, item);
        }
      } catch (error) {
        const bound = /(?:Staged payload|Archive) (\d+) bytes exceeds (\d+)|Packaging (\d+)ms exceeds (\d+)ms/.exec(error.stderr ?? '');
        if (bound) {
          record.budget = bound[3] ? 'package-time-ms' : 'payload-bytes';
          record.observed = Number(bound[1] ?? bound[3]);
          record.limit = Number(bound[2] ?? bound[4]);
        }
        const timedOut = error.code === 'ETIMEDOUT' || error.code === 124 || error.code === 137 || (error.killed && error.signal === 'SIGTERM');
        const packageBudget = packaging && (timedOut ||
          /Staged payload.*exceeds|Archive.*exceeds|Packaging.*exceeds|Packaged archive exceeds limit/.test(
            `${error.stderr ?? ''} ${error.message ?? ''}`));
        record.reason = error.code === 'TRANSPORT_BUDGET' ? 'transport-budget-exhausted' : error.identityFailure ? 'identity-mismatch' : packageBudget ? 'package-budget-exceeded' :
          timedOut ? 'timeout' : 'artifact-unavailable-or-invalid';
        if (error.groupNotQuiescent) { record.reason = 'owned-process-group-not-quiescent'; record.preservedPayloadDirectory = temp; throw error; }
        if (error.identityFailure || (packaging && !packageBudget && error.code !== 'TRANSPORT_BUDGET')) throw error;
      } finally {
        receipts.push(record);
        report(record);
        if (!record.preservedPayloadDirectory) await rm(temp, {recursive: true, force: true});
        await save();
      }
    }
  } finally { await save(); }
  return {restoredProjectsFile: restoredFile, receiptFile, producerAttempt: attempt};
}

// SDK calls run in killable children so a timed-out request cannot continue
// writing into a cleaned temp directory or overlap the next project's transfer.
export async function execute(options) {
  const transfer = async (method, args, timeout) => {
    const response = await exec(process.execPath,
      [fileURLToPath(import.meta.url), '--sdk', method, JSON.stringify(args)],
      {env: process.env, timeout, maxBuffer: 8 * 1024 * 1024});
    return JSON.parse(response.stdout.split('\n').findLast(line => line.startsWith('{')));
  };
  // SDK import stays inside the child; the placeholder is never called.
  return run(options, {artifact: {}, transfer});
}

if (process.argv[2] === '--sdk') {
  const {DefaultArtifactClient} = await import('@actions/artifact');
  try {
    const result = await new DefaultArtifactClient()[process.argv[3]](...JSON.parse(process.argv[4]));
    console.log(JSON.stringify(result));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
