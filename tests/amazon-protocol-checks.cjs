const fs=require('fs'),vm=require('vm'),assert=require('assert/strict'),path=require('path');
const requests=[],context=vm.createContext({URLSearchParams,AbortController,Date,console,view:'amazon',document:{hidden:false},setInterval:()=>0,render(){},data:{'integrations/connections':[{id:'real',settings:{marketplace:'amazon',mode:'real',environment:'production',name:'Fixture'}}]},fetch:async url=>{requests.push(url);return {ok:true,json:async()=>({items:[],nextToken:requests.length===1?'next-1':null})};}});
vm.runInContext(fs.readFileSync(path.resolve(__dirname,'../src/MarketPilot.Web/wwwroot/assets/amazon/amazon-center.js'),'utf8'),context);
const run=code=>vm.runInContext(code,context),settle=()=>new Promise(resolve=>setTimeout(resolve,0));
(async()=>{
 run("amTab='orders';amLoad(amCurrent(),'orders')");await settle();const first=new URL('http://local'+requests[0]);assert(first.searchParams.get('startDate'));assert(first.searchParams.get('endDate'));assert.equal(first.searchParams.get('token'),null);
 run("amPage(1);amLoad(amCurrent(),'orders')");await settle();const second=new URL('http://local'+requests[1]);assert.equal(second.searchParams.get('token'),'next-1');assert.equal(second.searchParams.get('startDate'),first.searchParams.get('startDate'));assert.equal(second.searchParams.get('endDate'),first.searchParams.get('endDate'));assert.equal(run("amState(amCurrent(),'orders').page"),1);
 run("amState(amCurrent(),'orders').fields={status:'SHIPPED'};amReset(amState(amCurrent(),'orders'),true)");assert.equal(run("amState(amCurrent(),'orders').page"),0);assert.equal(run("Object.keys(amState(amCurrent(),'orders').tokens).length"),1);
 context.fetch=async()=>({ok:true,json:async()=>({items:[],nextToken:'repeat'})});run("amLoad(amCurrent(),'orders')");await settle();run("amPage(1);amLoad(amCurrent(),'orders')");await settle();assert(run("amState(amCurrent(),'orders').error.includes('tekrarlandı')"));
 run("const state=amState(amCurrent(),'products');for(let i=0;i<500;i++)state.tokens[i]='token-'+i;state.page=499;amLoad(amCurrent(),'products')");await settle();assert(run("Object.keys(amState(amCurrent(),'products').tokens).length<=200"));
 console.log('PASS Amazon frontend protocol: pinned date window, native continuation, filter reset, repeated-token rejection and bounded history; no external network.');
})().catch(e=>{console.error(e);process.exitCode=1});
