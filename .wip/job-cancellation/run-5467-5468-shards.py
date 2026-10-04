import json,pathlib,subprocess,sys,time,xml.etree.ElementTree as ET
root=pathlib.Path.cwd()
evidence=pathlib.Path('/home/mike/honua-io/evidence-5467-5468')
evidence.mkdir(exist_ok=True)
merge_base=subprocess.check_output(['git','merge-base','origin/trunk','HEAD'],text=True).strip()
changed=subprocess.check_output(['git','diff','--name-only',merge_base],text=True)
selection=json.loads(subprocess.check_output(['scripts/ci/honua-server-targeted-tests.sh','--stdin'],input=changed,text=True))
(evidence/'selection.json').write_text(json.dumps(selection,indent=2))
config=json.loads((root/'.github/ci-shards.json').read_text())
shards=[s for s in config['shards'] if selection.get('run_all') or s['shard_name'] in selection['shards']]
print('Selected shards: '+', '.join(s['shard_name'] for s in shards),flush=True)
last_checkpoint=time.monotonic()

def checkpoint(message):
    global last_checkpoint
    subprocess.run(['git','add','-u'],check=True)
    subprocess.run(['git','-c','user.name=Mike McDougall','-c','user.email=mike@honua.io','commit','--allow-empty','-m',message],check=True)
    subprocess.run(['python3','/tmp/honua-network-retry.py','git','push','origin','HEAD:refs/heads/wip/fix/5467-5468-job-cancellation'],check=True)
    last_checkpoint=time.monotonic()

records=[]
pending=list(shards)
running=[]
failed=False
while pending or running:
    while pending and len(running)<2 and not failed:
        shard=pending.pop(0)
        suffix=shard['artifact_suffix']
        log=evidence/(suffix+'.log')
        results=evidence/suffix
        cmd=['dotnet','test',(shard.get('csproj') or 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'),'--no-build','--no-restore','--configuration','Release',
             '--filter','('+shard['filter']+')&Tier!=Slow','--logger','trx;LogFileName=results.trx','--results-directory',str(results)]
        print('Running '+shard['shard_name'],flush=True)
        out=log.open('w')
        process=subprocess.Popen(cmd,stdout=out,stderr=subprocess.STDOUT)
        running.append((shard,log,results,process,out))
    for item in list(running):
        shard,log,results,process,out=item
        if process.poll() is None: continue
        running.remove(item)
        out.close()
        trx=results/'results.trx'
        counters=ET.parse(trx).getroot().find('.//{*}Counters').attrib if trx.exists() else {}
        record={'shard':shard['shard_name'],'exit_code':process.returncode,'counts':counters,'log':str(log),'trx':str(trx)}
        records.append(record)
        (evidence/'shard-counts.json').write_text(json.dumps(records,indent=2))
        print(json.dumps(record),flush=True)
        if process.returncode:
            failed=True
            print(log.read_text()[-15000:],flush=True)
        else:
            checkpoint('test: checkpoint '+shard['shard_name']+' '+counters.get('passed','?')+' passed (#5467, #5468)')
    if failed and not running:
        sys.exit(1)
    if time.monotonic()-last_checkpoint>=15*60:
        checkpoint('test: WIP checkpoint during affected shard validation (#5467, #5468)')
    if running: time.sleep(5)
print('All '+str(len(records))+' selected shards completed',flush=True)
