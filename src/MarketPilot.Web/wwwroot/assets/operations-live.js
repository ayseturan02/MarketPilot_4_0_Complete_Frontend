// Invalidate cached views when durable backend state changes, including completed background jobs.
let orderRevision=null,orderRevisionView='',orderRevisionBusy=false;
const operationViews=['orders','shipping','dashboard','reports','connections','sync','products','mapping','inventory','barcodes','pricing','categories','listings','bulk','trendyol','hepsiburada','amazon','n11','boyner','ticimax'];
async function refreshOrderRevision(){
 if(orderRevisionBusy||document.hidden||modal||!operationViews.includes(view)||typeof localAccess!=='undefined'&&localAccess.configured&&!localAccess.authenticated)return;
 if(typeof auDirty!=='undefined'&&(auDirty||auSaving))return;
 if(['INPUT','TEXTAREA','SELECT'].includes(document.activeElement?.tagName))return;
 orderRevisionBusy=true;
 const active=view;
 try{
  const result=await api('operations/revision');
  if(view!==active||modal)return;
  if(active==='sync'&&typeof syncLoad==='function')await syncLoad(true);
  if(result.revision===orderRevision&&orderRevisionView===active)return;
  if(typeof chInvalidate==='function')chInvalidate();
  if(typeof chInvalidateStore==='function')for(const store of data['integrations/connections']||[])if(store.settings.mode==='demo')chInvalidateStore(store.id);
  if(['dashboard','reports'].includes(active))data.overview=await api('overview');
  if(active==='connections'&&typeof csLoad==='function')await csLoad();
  if(view!==active||modal)return;
  orderRevision=result.revision;orderRevisionView=active;
  render();
 }catch{
  // Keep the last visible data; the next scheduled poll will retry.
 }finally{orderRevisionBusy=false;}
}
setInterval(refreshOrderRevision,3000);
if(typeof document.addEventListener==='function')document.addEventListener('visibilitychange',()=>{if(!document.hidden)void refreshOrderRevision();});
