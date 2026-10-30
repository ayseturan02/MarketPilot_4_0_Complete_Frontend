from pathlib import Path
import hashlib,json,re,subprocess,datetime
root=Path(__file__).resolve().parents[2]
out=root/'docs/qa/evidence';out.mkdir(parents=True,exist_ok=True)
def git(*args):return subprocess.check_output(['git','-c',f'safe.directory={root.as_posix()}',*args],cwd=root,text=True,encoding='utf-8').strip()
files=[p for p in (root/'src').rglob('*') if p.is_file() and not {'Data','bin','obj','node_modules'}.intersection(p.parts)]
hashes={p.relative_to(root).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
snapshot={'at':datetime.datetime.now(datetime.timezone.utc).isoformat(),'head':git('rev-parse','HEAD'),'status':git('status','--short'),'sourceHashes':hashes}
base=out/'source-baseline.json'
if not base.exists():base.write_text(json.dumps(snapshot,ensure_ascii=False,indent=2),encoding='utf-8')
else:
 original=json.loads(base.read_text(encoding='utf-8'))['sourceHashes']
 snapshot['sourceChanges']=[name for name in set(original)|set(hashes) if original.get(name)!=hashes.get(name)]
 (out/'source-final.json').write_text(json.dumps(snapshot,ensure_ascii=False,indent=2),encoding='utf-8')
inventory={'files':{},'types':[],'endpoints':[],'groups':[],'frontendApiLiterals':[],'markers':[],'testProjects':[],'testScripts':[]}
for p in files:
 name=p.relative_to(root).as_posix();inventory['files'][name]={'bytes':p.stat().st_size}
 if p.suffix not in {'.cs','.js','.html','.css'}:continue
 text=p.read_text(encoding='utf-8-sig');inventory['files'][name]['lines']=len(text.splitlines())
 for i,line in enumerate(text.splitlines(),1):
  if p.suffix=='.cs':
   for m in re.finditer(r'\b(?:class|interface|record|enum)\s+(\w+)',line):inventory['types'].append({'file':name,'line':i,'name':m[1]})
   for m in re.finditer(r'\.Map(Get|Post|Put|Patch|Delete|Methods)\(\s*"([^"]*)"',line):inventory['endpoints'].append({'file':name,'line':i,'method':m[1].upper(),'path':m[2]})
   for m in re.finditer(r'\.MapGroup\(\s*"([^"]*)"',line):inventory['groups'].append({'file':name,'line':i,'path':m[1]})
  if p.suffix=='.js':
   for m in re.finditer(r'\b(?:api|chApi|hpApi|epApi)\(\s*[\'\"]([^\'\"\n]+)',line):inventory['frontendApiLiterals'].append({'file':name,'line':i,'path':m[1]})
  for m in re.finditer(r'\b(?:TODO|FIXME|HACK|NotImplementedException|NotSupportedException)\b',line):inventory['markers'].append({'file':name,'line':i,'marker':m[0]})
inventory['testProjects']=[p.parent.name for p in (root/'tests').glob('*/*.csproj')]
inventory['testScripts']=[p.name for p in (root/'tests').glob('*-checks.cjs')]
(out/'inventory.json').write_text(json.dumps(inventory,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'sourceFiles':len(files),'types':len(inventory['types']),'endpointDeclarations':len(inventory['endpoints']),'groups':len(inventory['groups']),'testProjects':len(inventory['testProjects']),'testScripts':len(inventory['testScripts']),'sourceChanges':snapshot.get('sourceChanges',[])},ensure_ascii=False))
