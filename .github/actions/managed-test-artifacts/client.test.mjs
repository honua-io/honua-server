import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, readFile, readdir, rm} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {run} from './client.mjs';
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
