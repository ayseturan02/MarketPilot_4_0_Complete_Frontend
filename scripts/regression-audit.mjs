import {spawn} from 'node:child_process';
import {mkdirSync,writeFileSync,readFileSync,existsSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const output=path.join(root,'artifacts','regression-audit');mkdirSync(output,{recursive:true});
const env={...process.env,ASPNETCORE_ENVIRONMENT:'Production',DOTNET_ENVIRONMENT:'Production',Logging__EventLog__LogLevel__Default:'None',Deployment__Mode:'local'};
for(const key of Object.keys(env))if(/^(MarketplaceAccounts__|Marketplaces__|Enterprise__|Webhooks__|Businesses__)/i.test(key))delete env[key];
Object.assign(env,{Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false',Enterprise__AllowDemoExchange:'false'});
const tasks=[
 ...['Integration','Catalog','Synchronization','Automation','LocalReliability','AccountWrite','Demo'].map(name=>({id:name,exe:'dotnet',args:['run','--project','tests/MarketPilot.'+name+'Checks','--no-restore','-p:BuildProjectReferences=false']})),
 ...['central-stock','enterprise-sync','local-access','accounts','shared-workspace','synchronization','webhook'].map(name=>({id:name,exe:process.execPath,args:['tests/'+name+'-http-checks.cjs']})),
 {id:'business-isolation-audit',exe:process.execPath,args:['tests/tenant-isolation-audit.cjs']},
 {id:'worker-isolation-audit',exe:'dotnet',args:['run','--project','tests/MarketPilot.WorkerIsolationAudit','-p:BuildProjectReferences=false']}
];
const selected=process.argv.slice(2),chosen=selected.length?tasks.filter(t=>selected.includes(t.id)):tasks;
if(selected.some(id=>!tasks.some(t=>t.id===id)))throw Error('Unknown audit task');
const resultFile=path.join(output,'results.json');
const results=selected.length&&existsSync(resultFile)?JSON.parse(readFileSync(resultFile,'utf8')).results.filter(r=>!selected.includes(r.id)):[];
async function run(task){
 const startedAt=new Date().toISOString();let log='';
 const child=spawn(task.exe,task.args,{cwd:root,env,windowsHide:true,stdio:['ignore','pipe','pipe']});
 child.stdout.on('data',data=>{log+=data.toString();});child.stderr.on('data',data=>{log+=data.toString();});
 const code=await new Promise(resolve=>{child.once('error',error=>{log+=error.message;resolve(-1);});child.once('close',resolve);});
 writeFileSync(path.join(output,task.id+'.log'),log);
 const result={id:task.id,exitCode:code,passed:code===0,startedAt,finishedAt:new Date().toISOString(),command:[task.exe,...task.args],log:path.relative(root,path.join(output,task.id+'.log'))};
 if(task.id==='business-isolation-audit'&&code===0){try{result.observations=JSON.parse(log.trim());result.passed=result.observations.businessIsolation.meetsRequirement;}catch{result.passed=false;}}
 if(task.id==='worker-isolation-audit'&&code===0){try{result.observations=JSON.parse(log.trim().split(/\r?\n/).findLast(line=>line.startsWith('{')));result.passed=result.observations.meetsRequirement;}catch{result.passed=false;}}
 results.push(result);console.log((result.passed?'PASS ':'FAIL ')+task.id);
 writeFileSync(path.join(output,'results.json'),JSON.stringify({generatedAt:new Date().toISOString(),liveApisUsed:false,results},null,2));
}
let next=0;await Promise.all(Array.from({length:3},async()=>{while(next<chosen.length)await run(chosen[next++]);}));
const business=results.find(r=>r.id==='business-isolation-audit')?.observations?.businessIsolation;
console.log(JSON.stringify({passedSuites:results.filter(r=>r.passed).length,totalSuites:tasks.length,businessIsolationMeetsRequirement:business?.meetsRequirement,report:'artifacts/regression-audit/results.json'}));
if(results.some(r=>!r.passed))process.exitCode=1;
