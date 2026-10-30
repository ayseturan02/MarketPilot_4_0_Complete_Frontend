var transportReached=false;
using var handler=new SocketsHttpHandler{UseProxy=false,ConnectCallback=(_,_)=>{transportReached=true;throw new Exception("FAKE_TRANSPORT_STOP; no socket created");}};
using var client=new HttpClient(handler);
var blocked=false;
try{await client.GetAsync("https://qa-egress-probe.invalid/");}catch(Exception e){blocked=e.ToString().Contains("QA_OFFLINE_EGRESS_BLOCKED");}
Console.WriteLine($"offlineHookBlocked={blocked}; fakeTransportReached={transportReached}; realSocketsCreated=0");
return blocked&&!transportReached?0:1;
