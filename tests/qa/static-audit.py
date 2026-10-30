from pathlib import Path
import json,subprocess,collections,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[2];out=root/'docs/qa/evidence'
inventory=json.loads((out/'inventory.json').read_text(encoding='utf-8'))
syntax=[]
for p in sorted((root/'src/MarketPilot.Web/wwwroot').rglob('*.js')):
 r=subprocess.run(['node','--check',str(p)],capture_output=True,text=True,encoding='utf-8')
 syntax.append({'file':p.relative_to(root).as_posix(),'exitCode':r.returncode,'error':r.stderr})
projects=[]
for p in list((root/'src').rglob('*.csproj'))+list((root/'tests').glob('*/*.csproj')):
 tree=ET.parse(p);projects.append({'file':p.relative_to(root).as_posix(),'packages':[x.attrib for x in tree.findall('.//PackageReference')]})
names=collections.defaultdict(list)
for row in inventory['types']:names[row['name']].append(row['file']+':'+str(row['line']))
result={'javascriptSyntax':syntax,'projects':projects,'sameTypeNamesAcrossNamespaces':{k:v for k,v in names.items() if len(v)>1},'totalSourceLines':sum(x.get('lines',0) for x in inventory['files'].values()),'sourceFiles':len(inventory['files']),'endpointDeclarations':len(inventory['endpoints']),'markers':inventory['markers'],'coverage':'No line/branch instrumentation; source syntax checks cannot validate generated DOM handlers.'}
(out/'static-analysis.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'jsFiles':len(syntax),'syntaxFailures':sum(x['exitCode']!=0 for x in syntax),'sourceLines':result['totalSourceLines'],'projects':len(projects),'explicitNugetReferences':sum(len(p['packages']) for p in projects),'duplicateTypeNames':list(result['sameTypeNamesAcrossNamespaces'])}))
