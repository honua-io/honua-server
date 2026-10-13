import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, readFile, readdir, rm, truncate} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {run, boundedRunner} from './client.mjs';
const project = 'tests/dotnet/Fixture.Tests/Fixture.Tests.csproj';
const sha = 'a'.repeat(40);
async function fixture(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'managed-transport-test-'));
  t.after(() => rm(root, {recursive: true, force: true}));
  await mkdir(path.join(root, '.github'));
  await writeFile(path.join(root, '.github/server-test-artifact-projects.json'), JSON.stringify({projects:[{csproj:project,artifact_suffix:'fixture'}]}));
  const projectsFile = path.join(root, 'projects.json');
  await writeFile(projectsFile, JSON.stringify([project]));
  const env = {RUNNER_TEMP:root,GITHUB_RUN_ID:'123',GITHUB_RUN_ATTEMPT:'2'};
  const opts = {root,projectsFile,mode:'consume'};
  const runner = async (file,args,identity) => {
    if (file === 'git') return {stdout:sha+'\n'};
    assert.equal(identity.HONUA_SERVER_TEST_ARTIFACT_WORKFLOW_RUN_ID,'123');
    if (args[0].includes('package-')) {
      const temp=args[args.indexOf('--output')+1];
      await writeFile(path.join(temp,'server-test-binaries-fixture.manifest.json'),JSON.stringify({archive_sha256:'b'.repeat(64)}));
      await writeFile(path.join(temp,'server-test-binaries-fixture.tar.gz'),'payload');
    }
    return {stdout:''};
  };
  const artifact = {
    getArtifact:async name => {assert.equal(name,'managed-tests-2-fixture');return {artifact:{id:42,size:100}};},
    downloadArtifact:async (id,{path:temp}) => {assert.equal(id,42);await writeFile(path.join(temp,'server-test-binaries-fixture.manifest.json'),JSON.stringify({archive_sha256:'b'.repeat(64)}));},
    uploadArtifact:async (name,files,temp,options) => {
      assert.equal(name,'managed-tests-2-fixture');assert.equal(files.length,2);
      assert.deepEqual(options,{retentionDays:1,compressionLevel:0});return {id:42};
    }
  };
  return {root,opts,deps:{env,runner,artifact},artifact,runner};
}
async function receipt(result) {return JSON.parse(await readFile(result.receiptFile,'utf8'));}
async function cleaned(root) {assert.equal((await readdir(root)).filter(n=>n.startsWith('managed-test-payload-')).length,0);}
test('consume restores only validated projects and retains hash/source receipt',async t=>{
 const f=await fixture(t);const r=await run(f.opts,f.deps);
 assert.deepEqual(JSON.parse(await readFile(r.restoredProjectsFile,'utf8')),[project]);
 const row=(await receipt(r)).projects[0];assert.equal(row.sourceSha,sha);assert.equal(row.archiveSha256,'b'.repeat(64));await cleaned(f.root);
});
test('publish uses two files, compression zero and one-day retention',async t=>{
 const f=await fixture(t);const r=await run({...f.opts,mode:'publish'},f.deps);
 assert.equal((await receipt(r)).projects[0].status,'published');await cleaned(f.root);
});
for (const kind of ['missing','network','oversize','corruption','missing-static','timeout']) test(`${kind} leaves caller fallback and cleans temp`,async t=>{
 const f=await fixture(t);
 if(kind==='missing'||kind==='network')f.artifact.getArtifact=async()=>{throw Error(kind);};
 if(kind==='oversize')f.artifact.getArtifact=async()=>({artifact:{id:1,size:999999999}});
 if(kind==='timeout')f.artifact.getArtifact=async()=>{throw Object.assign(Error('deadline'),{code:'ETIMEDOUT'});};
 if(kind==='corruption'||kind==='missing-static')f.deps.runner=async(file,...args)=>{if(file==='git')return f.runner(file,...args);throw Object.assign(Error('corrupt'),{stderr:'Artifact SHA-256 integrity check failed.'});};
 const r=await run(f.opts,f.deps);assert.deepEqual(JSON.parse(await readFile(r.restoredProjectsFile,'utf8')),[]);
 assert.equal((await receipt(r)).projects[0].status,'unrestored');await cleaned(f.root);
});
test('identity mismatch is fatal, receipt retained and temp removed',async t=>{
 const f=await fixture(t);f.deps.runner=async(file,...args)=>{if(file==='git')return f.runner(file,...args);throw Object.assign(Error('identity'),{stderr:'Artifact manifest does not match the exact source/project/toolchain contract.'});};
 await assert.rejects(run(f.opts,f.deps),/identity/);await cleaned(f.root);
 const dir=(await readdir(f.root)).find(n=>n.startsWith('managed-test-receipt-'));
 assert.equal(JSON.parse(await readFile(path.join(f.root,dir,'receipt.json'),'utf8')).projects[0].reason,'identity-mismatch');
});
test('disabled rollback switch does not use git, SDK or restore',async t=>{
 const f=await fixture(t);f.deps.runner=async()=>{throw Error('unexpected');};
 const r=await run({...f.opts,enabled:'false'},f.deps);assert.deepEqual(JSON.parse(await readFile(r.restoredProjectsFile,'utf8')),[]);
});
test('bad plan, config, booleans and run identity fail closed',async t=>{
 const f=await fixture(t);
 for(const override of [{configuration:'Debug'},{enabled:'yes'},{buildProperties:'RunAnalyzers=false'}])await assert.rejects(run({...f.opts,...override},f.deps));
 await assert.rejects(run(f.opts,{...f.deps,env:{...f.deps.env,GITHUB_RUN_ID:'0'}}));
 await writeFile(f.opts.projectsFile,JSON.stringify([project,project]));await assert.rejects(run(f.opts,f.deps));
});
test('explicit producer attempt supports same-run rerun retrieval',async t=>{
 const f=await fixture(t);f.artifact.getArtifact=async name=>{assert.equal(name,'managed-tests-1-fixture');return {artifact:{id:42,size:100}};};
 const r=await run({...f.opts,producerAttempt:'1'},f.deps);assert.equal(r.producerAttempt,'1');
});
test('projects transfer sequentially and never overlap payload directories',async t=>{
 const f=await fixture(t);const other='tests/dotnet/Other.Tests/Other.Tests.csproj';
 await writeFile(f.opts.projectsFile,JSON.stringify([project,other]));
 await writeFile(path.join(f.root,'.github/server-test-artifact-projects.json'),JSON.stringify({projects:[{csproj:project,artifact_suffix:'fixture'},{csproj:other,artifact_suffix:'other'}]}));
 const seen=[];
 f.artifact.getArtifact=async name=>{
   assert.equal((await readdir(f.root)).filter(n=>n.startsWith('managed-test-payload-')).length,1);
   seen.push(name);return {artifact:{id:seen.length,size:100}};
 };
 f.artifact.downloadArtifact=async(id,{path:temp})=>writeFile(path.join(temp,`server-test-binaries-${id===1?'fixture':'other'}.manifest.json`),JSON.stringify({archive_sha256:'b'.repeat(64)}));
 const r=await run(f.opts,f.deps);assert.deepEqual(JSON.parse(await readFile(r.restoredProjectsFile,'utf8')),[project,other]);await cleaned(f.root);
});
test('packaging contract error is fatal rather than silently publishing nothing',async t=>{
 const f=await fixture(t);f.deps.runner=async(file,...args)=>{if(file==='git')return f.runner(file,...args);throw Error('missing built output');};
 await assert.rejects(run({...f.opts,mode:'publish'},f.deps),/missing built output/);await cleaned(f.root);
});

test('truncated downloaded JSON falls back while valid wrong identity remains fatal',async t=>{
 const f=await fixture(t);f.artifact.downloadArtifact=async(id,{path:temp})=>writeFile(path.join(temp,'server-test-binaries-fixture.manifest.json'),'{broken');
 const r=await run(f.opts,f.deps);assert.deepEqual(JSON.parse(await readFile(r.restoredProjectsFile,'utf8')),[]);
 assert.equal((await receipt(r)).projects[0].reason,'artifact-unavailable-or-invalid');await cleaned(f.root);
});
for(const failure of ['archive-size','package-size','timeout'])test(`publish ${failure} cleans and continues next project`,async t=>{
 const f=await fixture(t);const other='tests/dotnet/Other.Tests/Other.Tests.csproj';
 await writeFile(f.opts.projectsFile,JSON.stringify([project,other]));
 await writeFile(path.join(f.root,'.github/server-test-artifact-projects.json'),JSON.stringify({projects:[{csproj:project,artifact_suffix:'fixture'},{csproj:other,artifact_suffix:'other'}]}));
 const uploaded=[];
 f.deps.runner=async(file,args,identity)=>{
   if(file==='git')return f.runner(file,args,identity);
   const current=args[args.indexOf('--project')+1],temp=args[args.indexOf('--output')+1];
   assert.equal((await readdir(f.root)).filter(n=>n.startsWith('managed-test-payload-')).length,1);
   if(current===project && failure==='package-size')throw Object.assign(Error('bound'),{stderr:'Staged payload 999999999 bytes exceeds 805306368.'});
   if(current===project && failure==='timeout')throw Object.assign(Error('deadline'),{killed:true,signal:'SIGTERM'});
   const suffix=current===project?'fixture':'other';
   await writeFile(path.join(temp,`server-test-binaries-${suffix}.manifest.json`),JSON.stringify({archive_sha256:'b'.repeat(64)}));
   const archive=path.join(temp,`server-test-binaries-${suffix}.tar.gz`);await writeFile(archive,'payload');
   if(current===project)await truncate(archive,335544321);
   return {stdout:''};
 };
 f.artifact.uploadArtifact=async name=>{uploaded.push(name);return {id:42};};
 const r=await run({...f.opts,mode:'publish'},f.deps);const rows=(await receipt(r)).projects;
 assert.deepEqual(uploaded,['managed-tests-2-other']);assert.equal(rows[0].reason,'package-budget-exceeded');
 assert.equal(rows[0].status,'unrestored');assert.equal(rows[1].status,'published');await cleaned(f.root);
});
test('overall deadline stops requests and staging for every remaining project',async t=>{
 const f=await fixture(t);const other='tests/dotnet/Other.Tests/Other.Tests.csproj';let now=0,calls=0;
 await writeFile(f.opts.projectsFile,JSON.stringify([project,other]));
 await writeFile(path.join(f.root,'.github/server-test-artifact-projects.json'),JSON.stringify({projects:[{csproj:project,artifact_suffix:'fixture'},{csproj:other,artifact_suffix:'other'}]}));
 f.deps.now=()=>now;
 f.artifact.getArtifact=async()=>{calls++;now=600000;return {artifact:{id:42,size:100}};};
 f.artifact.downloadArtifact=async()=>{throw Error('must not download after deadline');};
 const r=await run(f.opts,f.deps);assert.equal(calls,1);assert.deepEqual(JSON.parse(await readFile(r.restoredProjectsFile,'utf8')),[]);
 assert.deepEqual((await receipt(r)).projects.map(p=>p.reason),['transport-budget-exhausted','transport-budget-exhausted']);await cleaned(f.root);
});
test('SDK and packaging timeouts are capped to remaining overall budget',async t=>{
 const f=await fixture(t);let now=0;const limits=[];
 f.deps.now=()=>now;
 f.deps.transfer=async(method,args,timeout)=>{
   limits.push(timeout);
   if(method==='getArtifact'){now=590000;return {artifact:{id:42,size:100}};}
   assert.equal(method,'downloadArtifact');await f.artifact.downloadArtifact(...args);
 };
 f.deps.runner=async(file,args,identity,timeout)=>{
   if(file==='git')return f.runner(file,args,identity);
   assert.equal(timeout,10000);return f.runner(file,args,identity);
 };
 const r=await run(f.opts,f.deps);assert.deepEqual(limits,[180000,10000]);assert.equal((await receipt(r)).projects[0].status,'restored');await cleaned(f.root);
});
test('publish expiration after packaging does not upload or become a fatal contract failure',async t=>{
 const f=await fixture(t);let now=0;f.deps.now=()=>now;
 f.deps.runner=async(...args)=>{const result=await f.runner(...args);if(args[0]!=='git')now=600000;return result;};
 f.artifact.uploadArtifact=async()=>{throw Error('must not upload after deadline');};
 const r=await run({...f.opts,mode:'publish'},f.deps);
 assert.equal((await receipt(r)).projects[0].reason,'transport-budget-exhausted');await cleaned(f.root);
});

test('bounded shell runner kills TERM-ignoring descendants before payload reuse', async t => {
 const root = await mkdtemp(path.join(os.tmpdir(), 'managed-process-group-'));
 t.after(() => rm(root, {recursive:true, force:true}));
 const payload = path.join(root, 'payload');await mkdir(payload);
 const marker = path.join(payload, 'late-write');
 // The owned child ignores TERM and would write after the initial timeout.
 // The runner must escalate against the whole group, not just the parent bash.
 const script = 'trap "" TERM; (trap "" TERM; printf ready > "$READY"; sleep 5; printf late > "$MARKER") & wait';
 await assert.rejects(boundedRunner('bash', ['-c', script], {
   cwd:root,env:{...process.env,MARKER:marker,READY:path.join(root,'ready')},timeout:250
 }), error => error.code === 'ETIMEDOUT');
 assert.equal(await readFile(path.join(root,'ready'),'utf8'),'ready');
 // Model the action finally cleanup and immediate next project's directory reuse.
 await rm(payload, {recursive:true,force:true});await mkdir(payload);
 await writeFile(path.join(payload,'next-project'), 'owned');
 await new Promise(resolve => setTimeout(resolve, 2300));
 assert.deepEqual(await readdir(payload), ['next-project']);
});

test('publication gives Server.Tests the budget first; remaining order and consume order stay unchanged',async t=>{
 const f=await fixture(t);
 const server='tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj';
 const other='tests/dotnet/Other.Tests/Other.Tests.csproj';
 const requested=[project,other,server];
 const suffixes=new Map([[project,'fixture'],[other,'other'],[server,'server']]);
 await writeFile(f.opts.projectsFile,JSON.stringify(requested));
 await writeFile(path.join(f.root,'.github/server-test-artifact-projects.json'),JSON.stringify({projects:requested.map(csproj=>({csproj,artifact_suffix:suffixes.get(csproj)}))}));
 let now=0;const built=[],published=[];f.deps.now=()=>now;
 f.deps.runner=async(file,args,identity)=>{
   if(file==='git')return f.runner(file,args,identity);
   const current=args[args.indexOf('--project')+1],temp=args[args.indexOf('--output')+1];
   built.push(current);const suffix=suffixes.get(current);
   await writeFile(path.join(temp,`server-test-binaries-${suffix}.manifest.json`),JSON.stringify({archive_sha256:'b'.repeat(64)}));
   await writeFile(path.join(temp,`server-test-binaries-${suffix}.tar.gz`),'payload');return {stdout:''};
 };
 f.artifact.uploadArtifact=async name=>{published.push(name);now=600000;return {id:42};};
 const result=await run({...f.opts,mode:'publish'},f.deps);
 assert.deepEqual(built,[server]);assert.deepEqual(published,['managed-tests-2-server']);
 assert.deepEqual((await receipt(result)).projects.map(p=>[p.project,p.status,p.reason]),[
   [server,'published',undefined],[project,'unrestored','transport-budget-exhausted'],[other,'unrestored','transport-budget-exhausted']]);
 // With enough budget, only Server.Tests moves; other caller order is stable.
 now=0;built.length=0;published.length=0;
 f.artifact.uploadArtifact=async name=>{published.push(name);return {id:42};};
 await run({...f.opts,mode:'publish'},f.deps);assert.deepEqual(built,[server,project,other]);
 const retrieved=[];f.deps.runner=f.runner;
 f.artifact.getArtifact=async name=>{retrieved.push(name);return {artifact:{id:retrieved.length,size:100}};};
 f.artifact.downloadArtifact=async(id,{path:temp})=>writeFile(path.join(temp,`server-test-binaries-${suffixes.get(requested[id-1])}.manifest.json`),JSON.stringify({archive_sha256:'b'.repeat(64)}));
 await run(f.opts,f.deps);assert.deepEqual(retrieved,['managed-tests-2-fixture','managed-tests-2-other','managed-tests-2-server']);await cleaned(f.root);
});
