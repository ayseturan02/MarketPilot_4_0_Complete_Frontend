// One refresh coordinator for all screens and tabs. Editing a form never triggers a repaint.
let orderRevision=null,orderRevisionView='',orderRevisionBusy=false,workspaceDirty=true,workspaceLastCheck=null,workspaceSyncError='';
const operationViews=Object.keys(names);
let workspaceMutationTimer=null,workspaceDashboardReport=null,workspaceSubmittingForm=null;
function workspaceEditing(){
 return !!modal||!!document.querySelector('.ch-dialog')||['INPUT','TEXTAREA','SELECT'].includes(document.activeElement?.tagName)||
  !!document.querySelector('.content form[data-workspace-dirty="true"]')||
  typeof axEditing==='function'&&axEditing()||typeof chBusy!=='undefined'&&chBusy||typeof csBusy!=='undefined'&&csBusy||
  typeof auSaving!=='undefined'&&auSaving||typeof syncBusy!=='undefined'&&syncBusy||typeof wuBusy!=='undefined'&&wuBusy;
}
function workspaceExpire(){
 if(typeof chStates!=='undefined')for(const state of chStates.values()){const selected=new Set(state.selected);chReset(state);state.selected=selected;}
 if(typeof hpStates!=='undefined')for(const holder of hpStates.values()){holder.controller?.abort();holder.entry=null;holder.signature='';}
 if(typeof chInvalidateStore==='function')for(const store of data['integrations/connections']||[])if(store.settings.mode==='demo')chInvalidateStore(store.id);
 if(typeof wuInvalidate==='function')wuInvalidate();
 if(typeof psCache!=='undefined')psCache.clear();
}
function workspaceMarkDirty(){
 workspaceDirty=true;clearTimeout(workspaceMutationTimer);
 workspaceMutationTimer=setTimeout(()=>void refreshOrderRevision(),100);
}
async function refreshOrderRevision(force=false){
 if(force)workspaceDirty=true;
 if(orderRevisionBusy||document.hidden||!data.overview||!operationViews.includes(view)||workspaceEditing()||typeof localAccess!=='undefined'&&localAccess.configured&&!localAccess.authenticated)return;
 orderRevisionBusy=true;const active=view;
 try{
  const result=await api('operations/revision');
  if(!workspaceDirty&&result.revision===orderRevision&&orderRevisionView===active&&workspaceLastCheck&&Date.now()-Date.parse(workspaceLastCheck)<15000){workspaceSyncError='';return;}
  const access=await api('access/status');
  if(access.configured&&!access.authenticated){localAccess=access;workspaceExpire();render();return;}
  const promises=[load(),api('analytics/workspace')];
  if(active==='dashboard')promises.push(api('analytics/reports'));
  if(active==='reports'||active==='profit')promises.push(api('analytics/'+active+(active==='reports'?'?'+new URLSearchParams(axState.query):'')));
  if(active==='connections')promises.push(api('integrations/central-stock'));
  if(active==='sync')promises.push(api('sync/status'));
  const values=await Promise.all(promises);
  if(view!==active||workspaceEditing()){workspaceDirty=true;return;}
  localAccess=access;
  axState.workspace=values[1];data.settings={...data.settings,...values[1].settings};auData={automation:values[1].automation,outbox:values[1].outbox};
  delete axState.errors.workspace;
  if(active==='dashboard')workspaceDashboardReport=values[2];
  if(active==='reports'||active==='profit'){axState[active]=values[2];delete axState.errors[active];}
  if(active==='connections'){csData=values[2];csError='';}
  if(active==='sync'){syncState=values[2];syncError='';}
  workspaceExpire();
  orderRevision=result.revision;orderRevisionView=active;workspaceDirty=false;workspaceLastCheck=new Date().toISOString();workspaceSyncError='';
  if(view===active&&!workspaceEditing())render();
 }catch(error){workspaceSyncError=error.message;workspaceDirty=true;}
 finally{orderRevisionBusy=false;}
}
// Observe successful local writes without changing requests, payloads or response bodies.
if(typeof globalThis.fetch==='function'){
 const workspaceFetch=globalThis.fetch.bind(globalThis);
 globalThis.fetch=async function(input,options){
  const submitting=workspaceSubmittingForm;
  const response=await workspaceFetch(input,options);
  try{const url=new URL(typeof input==='string'||input instanceof URL?String(input):input.url,location.href),method=(options?.method||input?.method||'GET').toUpperCase();
   if(url.origin===location.origin&&url.pathname.startsWith('/api/')&&!['GET','HEAD','OPTIONS'].includes(method)){
    if(response.ok){if(submitting?.dataset)delete submitting.dataset.workspaceDirty;workspaceMarkDirty();}
    if(workspaceSubmittingForm===submitting)workspaceSubmittingForm=null;
   }
  }catch{}
  return response;
 };
}
if(typeof document.addEventListener==='function'){
 document.addEventListener('input',event=>{const form=event.target.closest?.('.content form');if(form&&!form.matches('.lf-filter-form,.wu-filter'))form.dataset.workspaceDirty='true';});
 document.addEventListener('submit',event=>{const form=event.target;workspaceSubmittingForm=form;setTimeout(()=>{if(workspaceSubmittingForm===form)workspaceSubmittingForm=null;},0);},true);
 document.addEventListener('visibilitychange',()=>{if(!document.hidden)void refreshOrderRevision(true);});
}
if(typeof window.addEventListener==='function')window.addEventListener('focus',()=>void refreshOrderRevision(true));
setInterval(()=>void refreshOrderRevision(),2500);
