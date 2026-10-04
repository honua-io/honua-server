import pathlib,subprocess,time
root=pathlib.Path.cwd()
evidence=pathlib.Path('/home/mike/honua-io/evidence-5467-5468')
base=subprocess.check_output(['git','merge-base','origin/trunk','HEAD'],text=True).strip()
files=subprocess.check_output(['git','diff','--name-only',base],text=True).splitlines()
projects={}
for name in files:
    if not name.endswith('.cs'):continue
    parts=pathlib.Path(name).parts
    project_dir=root.joinpath(*(parts[:2] if parts[0]=='src' else parts[:3]))
    matches=list(project_dir.glob('*.csproj'))
    assert len(matches)==1,(name,matches)
    projects.setdefault(matches[0],[]).append(str(root/name))
for project,includes in sorted(projects.items()):
    for verify in (False,True):
        command=['/tmp/honua-format-with-slot.sh',str(project),'--no-restore','--include',*includes]
        if verify:command.append('--verify-no-changes')
        print(('Verify ' if verify else 'Format ')+str(project.relative_to(root)),flush=True)
        log=evidence/(project.stem+('-format-verify.log' if verify else '-format.log'))
        with log.open('w') as out:
            subprocess.run(command,stdout=out,stderr=subprocess.STDOUT,check=True)
        print('Passed: '+str(log),flush=True)
