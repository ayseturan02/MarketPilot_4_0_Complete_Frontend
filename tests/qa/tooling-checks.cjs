const path=require('node:path'),fs=require('node:fs'),{spawnSync}=require('node:child_process');const root=path.resolve(__dirname,'../..'),env=require('./environment.cjs')(root),out=path.join(root,'docs/qa/evidence');
const rows=[];
for(const [name,exe,args] of [
 ['accounts-diagnostic',process.execPath,['tests/qa/instrument-accounts.cjs']],
 ['npm-offline-audit','cmd.exe',['/d','/c','npm.cmd audit --offline --json']],
 ['docker-compose-config','docker',['--config',path.join(root,'tests/qa/docker-config'),'compose','config','--quiet']],
 ['node-syntax',process.execPath,['tests/frontend-checks.cjs']]
]){const r=spawnSync(exe,args,{cwd:root,env:{...env,MARKETPILOT_DOMAIN:'marketpilot.example.test'},encoding:'utf8',windowsHide:true,timeout:60000,maxBuffer:8*1024*1024});fs.writeFileSync(path.join(out,name+'.log'),(r.stdout||'')+(r.stderr||''));const row={name,exitCode:r.status,error:r.error?.message};rows.push(row);console.log(row);}
fs.writeFileSync(path.join(out,'tooling-results.json'),JSON.stringify(rows,null,2));
