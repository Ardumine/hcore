using System.Collections.Concurrent;
using HCore.Main.Mthc.Calls;
using HCore.Main.Mthc.Calls.Requests;
using HCore.Main.Mthc.Calls.Responses;
using HCore.Modules.Base;

namespace HCore.Main.Mthc;


public class CallReceiver
{
    private AdamPipe<RequestContext> _adamPipeReceive;
    private ConcurrentDictionary<Guid, AdamPipe<BaseResponse>> _responsesByRequestId;

    public CallReceiver(AdamPipe<RequestContext> adamPipeReceive, 
        ConcurrentDictionary<Guid, AdamPipe<BaseResponse>> responsesByRequestId)
    {
        _adamPipeReceive = adamPipeReceive;
        _responsesByRequestId = responsesByRequestId;
    }
    
    public void HandleRequests(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var context = _adamPipeReceive.Wait(ct);
            Console.WriteLine("Request received!");
            
            var response = ProcessRequest(context.Request);
            
            if (_responsesByRequestId.TryGetValue(context.RequestId, out var pipe))
            {
                pipe.SendSignal(response);
            }
        }
    }
    
    private BaseResponse ProcessRequest(BaseRequest request)
    {
        return new BaseResponse();
    }
}