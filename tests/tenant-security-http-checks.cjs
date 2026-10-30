const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
(async()=>{
 const root=path.resolve(__dirname,'..'),temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-tenant-security-'));
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));const base='http://127.0.0.1:'+port;
 const env={...process.env,Logging__EventLog__LogLevel__Default:'None',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false',MARKETPILOT__TENANT_A__ERP__SECRET:'fixture-secret-must-not-appear'};
 const child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env});
 async function api(route,body,cookie,method,headers={}){const response=await fetch(base+'/api/'+route,{method:method||(body===undefined?'GET':'POST'),headers:{Origin:base,'Content-Type':'application/json',...(cookie?{Cookie:cookie}:{}),...headers},body:body===undefined?undefined:JSON.stringify(body)});return{status:response.status,body:await response.json().catch(()=>null),cookie:(response.headers.get('set-cookie')||'').split(';')[0]};}
 const password='Tenant-Fixture-Password-2026';
 try{
  let health;for(let i=0;i<100;i++){try{health=await api('health');if(health.status===200)break;}catch{}await new Promise(r=>setTimeout(r,100));}assert.equal(health?.status,200);
  const platform=await api('access/setup',{name:'tenant-root',password});assert.equal(platform.status,200);
  const users={};
  for(const [name,role,tenant] of [['owner-a','business-owner','tenant-a'],['owner-b','business-owner','tenant-b'],['admin-a','business-admin','tenant-a'],['ops-a','operations','tenant-a'],['warehouse-a','warehouse','tenant-a'],['reader-a','read-only','tenant-a']]){
   const user=await api('access/users',{name,password,role,businessId:tenant},platform.cookie);assert.equal(user.status,200,JSON.stringify(user));
   const login=await api('access/login',{name,password});assert.equal(login.status,200);users[name]={...user.body,cookie:login.cookie};
  }
  const a=users['owner-a'],b=users['owner-b'],admin=users['admin-a'],ops=users['ops-a'],warehouse=users['warehouse-a'],reader=users['reader-a'];
  assert.equal((await api('products')).status,401);
  for(const route of ['access/users','security/connections','tenancy'])assert.equal((await api(route,undefined,reader.cookie)).status,403);
  assert.equal((await api('wms/inventory',undefined,warehouse.cookie)).status,200);
  assert.equal((await api('analytics/profit',undefined,warehouse.cookie)).status,403);
  assert.equal((await api('integrations/connections',{name:'forbidden',marketplace:'trendyol',mode:'demo',environment:'stage'},ops.cookie)).status,403);
  assert.equal((await api('WMS/INVENTORY/POLICY',{safetyStockByWarehouse:{},warehouseAssignmentsByAccount:{},channelStockLimits:{}},warehouse.cookie,'PUT')).status,403,'Uppercase route bypassed roles');
  assert.equal((await api('ACCESS/USERS',{name:'escape',password,role:'platform-admin'},ops.cookie)).status,403);
  assert.equal((await api('access/users/'+a.id,{role:'read-only',enabled:true},admin.cookie)).status,403,'Admin modified owner');
  assert.equal((await api('access/users/'+admin.id,{role:'operations',enabled:true},admin.cookie)).status,403,'Admin demoted administrator');
  assert.equal((await api('access/users',{name:'escape',password,role:'platform-admin',businessId:'default'},a.cookie)).status,403);
  for(const headers of [{'X-Target-Tenant-Id':'tenant-b'},{'X-Source-Tenant-Id':'tenant-b'},{'X-Tenant-Id':'tenant-b'}])
   assert.equal((await api('PRODUCTS',undefined,a.cookie,undefined,headers)).status,403,'Tenant header spoof accepted');
  assert.equal((await api('PRODUCTS?targetTenantId=tenant-b',undefined,a.cookie)).status,403);
  assert.equal((await api('sync/enterprise/integration/events',{tenantId:'tenant-a',nested:{targetTenantId:'tenant-b'}},a.cookie)).status,403);
  const credential={id:'credential-a',tenantId:'tenant-a',system:'erp',provider:'environment',keyName:'MARKETPILOT__TENANT_A__ERP__SECRET'};
  assert.equal((await api('security/connections/credentials',credential,a.cookie,'PUT')).status,200);
  assert.equal((await api('security/connections/erp',{id:'erp-a',tenantId:'tenant-a',name:'Private A ERP',credentialReferenceId:'credential-a',enabled:false},a.cookie,'PUT')).status,200);
  assert.equal((await api('security/connections/credentials',{id:'wms-credential-a',tenantId:'tenant-a',system:'wms',provider:'mock',keyName:'MOCK_WMS_A'},a.cookie,'PUT')).status,200);
  assert.equal((await api('security/connections/wms',{id:'wms-a',tenantId:'tenant-a',name:'Private A WMS',credentialReferenceId:'wms-credential-a',enabled:false},a.cookie,'PUT')).status,200);
  const own=await api('security/connections',undefined,a.cookie),foreign=await api('security/connections',undefined,b.cookie);
  assert.equal(own.body.erp.length,1);assert.equal(own.body.wms.length,1);assert.equal(foreign.body.erp.length,0);assert.equal(foreign.body.wms.length,0);assert(!JSON.stringify(own).includes(env.MARKETPILOT__TENANT_A__ERP__SECRET));assert(!JSON.stringify(own.body).includes('keyName'));
  assert((await api('tenancy',undefined,a.cookie)).body.tenants.every(tenant=>tenant.id==='tenant-a'));
  assert.equal((await api('wms',undefined,b.cookie)).body.profile.tenantId,'tenant-b');
  assert((await api('erp/nebim/products',undefined,b.cookie)).body.data.every(product=>product.context.tenantId==='tenant-b'));
  assert.equal((await api('security/connections/wms',{id:'wms-a',tenantId:'tenant-a',name:'wrong system',credentialReferenceId:'credential-a',enabled:false},a.cookie,'PUT')).status,400);
  assert.equal((await api('security/connections/erp',{id:'erp-b',tenantId:'tenant-b',name:'cross tenant',credentialReferenceId:'credential-a',enabled:false},a.cookie,'PUT')).status,403);
  const event={tenantId:'tenant-a',accountId:'seller-a',eventId:'a-private-event',correlationId:'a-private-correlation',eventType:'ProductChanged',sourceSystem:'mock',sourceVersion:1,targetSystem:'audit',occurredAt:new Date().toISOString(),payload:{entityId:'private-a'}};
  assert.equal((await api('sync/enterprise/integration/events',event,a.cookie)).status,202);
  assert(!(await api('sync/enterprise/integration/events',undefined,b.cookie)).body.some(job=>job.integrationEvent.eventId===event.eventId));
  assert.equal((await api('tenancy/memberships',{userId:a.id,tenantId:'tenant-b',organizationId:'tenant-b-org',roleId:'read-only',enabled:true},platform.cookie)).status,200);
  assert.equal((await api('access/switch-tenant',{tenantId:'tenant-b'},ops.cookie)).status,403);
  assert.equal((await api('tenancy/memberships',{userId:ops.id,tenantId:'tenant-b',organizationId:'tenant-b-org',roleId:'business-admin',enabled:true},platform.cookie)).status,200);
  const secondaryAdmin=await api('access/switch-tenant',{tenantId:'tenant-b'},ops.cookie);assert.equal(secondaryAdmin.status,200);
  const secondaryUser=await api('access/users',{name:'secondary-managed-user',password,role:'read-only'},secondaryAdmin.cookie);assert.equal(secondaryUser.status,200);
  assert.equal((await api('access/users/'+secondaryUser.body.id,{role:'operations',enabled:true},secondaryAdmin.cookie)).status,200,'Active secondary membership could not manage its own tenant');
  const switched=await api('access/switch-tenant',{tenantId:'tenant-b'},a.cookie);assert.equal(switched.status,200);
  assert.equal((await api('access/status',undefined,switched.cookie)).body.tenantRole,'read-only');
  assert.equal((await api('security/connections',undefined,switched.cookie)).status,403);
  const changed=await api('access/password',{currentPassword:password,newPassword:'Changed-Tenant-Password-2026'},switched.cookie);assert.equal(changed.status,200);
  const active=(await api('access/status',undefined,changed.cookie)).body;assert.equal(active.businessId,'tenant-b');assert.equal(active.tenantRole,'read-only');
  assert.equal((await api('products',undefined,a.cookie)).status,401,'Old session survived password change');
  assert.equal((await api('tenancy/memberships',{userId:a.id,tenantId:'tenant-b',organizationId:'tenant-b-org',roleId:'read-only',enabled:false},platform.cookie)).status,200);
  assert.equal((await api('products',undefined,changed.cookie)).status,401,'Revoked membership retained access');
  assert(fs.existsSync(path.join(temp,'Data','tenant-directory.json')));
  console.log('PASS multi-tenant security HTTP: six roles, horizontal/vertical denial, uppercase routes, source/target spoofing, ERP credential reference isolation/redaction, durable event isolation, explicit memberships/switch, active-tenant password change, cookie and membership revocation. JSON only.');
 }finally{
  const stopped=new Promise(r=>child.once('exit',r));child.kill();await stopped;
  const resolved=path.resolve(temp);assert.equal(path.dirname(resolved),path.resolve(os.tmpdir()));assert(path.basename(resolved).startsWith('marketpilot-tenant-security-'));fs.rmSync(resolved,{recursive:true,force:true});
 }
})().catch(e=>{console.error(e);process.exitCode=1;});
