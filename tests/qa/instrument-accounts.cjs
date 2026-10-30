// Diagnostic replay of the unchanged regression: add the route/status to its JSON parser error.
const fs=require('node:fs'),path=require('node:path'),Module=require('node:module');
const filename=path.resolve(__dirname,'../accounts-http-checks.cjs');
const source=fs.readFileSync(filename,'utf8').replace('body:await r.json()','body:await r.json().catch(error=>{throw new Error("QA JSON parse failure: "+url+" status="+r.status+"; "+error.message)})');
const compiled=new Module(filename,module);compiled.filename=filename;compiled.paths=Module._nodeModulePaths(path.dirname(filename));compiled._compile(source,filename);
