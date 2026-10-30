const fs=require('node:fs'),path=require('node:path'),{spawnSync}=require('node:child_process');
const root=path.resolve(__dirname,'../..'),env=require('./environment.cjs')(root),out=path.join(root,'docs/qa/evidence');
for(const mode of (process.argv[2]?[process.argv[2]]:['invariants','backup','performance'])){
 const r=spawnSync('dotnet',['tests/qa/Probes/bin/Debug/net8.0/Probes.dll',mode],{cwd:root,env,windowsHide:true,encoding:'utf8',timeout:60000});
 fs.writeFileSync(path.join(out,mode+'-probe.log'),r.stdout+r.stderr);
 let data;try{data=JSON.parse(r.stdout);}catch{data={stdout:r.stdout,stderr:r.stderr};}
 fs.writeFileSync(path.join(out,mode+'-probe.json'),JSON.stringify({exitCode:r.status,...data},null,2));console.log(mode,r.status,JSON.stringify(data));if(r.status!==0)process.exitCode=1;
}
