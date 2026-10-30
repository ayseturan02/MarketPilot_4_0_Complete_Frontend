using System.Diagnostics;
using System.Net;
using System.Net.Http;

public static class StartupHook
{
    private static IDisposable? subscription;
    public static void Initialize()
    {
        if(Environment.GetEnvironmentVariable("MARKETPILOT_QA_OFFLINE")!="1")return;
        subscription=DiagnosticListener.AllListeners.Subscribe(new Listeners());
    }
    private sealed class Listeners:IObserver<DiagnosticListener>
    {
        public void OnCompleted(){}public void OnError(Exception error){}
        public void OnNext(DiagnosticListener listener)
        {if(listener.Name=="HttpHandlerDiagnosticListener")listener.Subscribe(new Requests());}
    }
    private sealed class Requests:IObserver<KeyValuePair<string,object?>>
    {
        public void OnCompleted(){}public void OnError(Exception error){}
        public void OnNext(KeyValuePair<string,object?> value)
        {
            if(value.Key!="System.Net.Http.HttpRequestOut.Start")return;
            var request=value.Value?.GetType().GetProperty("Request")?.GetValue(value.Value) as HttpRequestMessage;
            var host=request?.RequestUri?.DnsSafeHost;
            if(host=="localhost"||IPAddress.TryParse(host?.Trim('[',']'),out var ip)&&IPAddress.IsLoopback(ip))return;
            // Throw synchronously before DiagnosticsHandler invokes the socket transport.
            var log=Environment.GetEnvironmentVariable("MARKETPILOT_QA_EGRESS_LOG");
            if(log!=null)try{File.AppendAllText(log,"dotnet non-loopback request blocked\n");}catch{}
            throw new InvalidOperationException("QA_OFFLINE_EGRESS_BLOCKED");
        }
    }
}
