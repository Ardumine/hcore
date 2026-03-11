using System.Collections.Concurrent;
using HCore.Main.Extras;
using HCore.Main.Mthc.Calls;
using HCore.Main.Mthc.Calls.Requests;
using HCore.Main.Mthc.Calls.Responses;
using HCore.Modules.Base;

namespace HCore.Main.Mthc;

public class CallClient
{
    private AdamPipe<RequestContext> _adamPipeRequest;
    private ObjectPool<AdamPipe<BaseResponse>> _adamPipeResponsePool;
    private ConcurrentDictionary<Guid, AdamPipe<BaseResponse>> _responsesByRequestId;


    
    public CallClient(AdamPipe<RequestContext> adamPipeRequest,
        ConcurrentDictionary<Guid, AdamPipe<BaseResponse>> responsesByRequestId,
        ObjectPool<AdamPipe<BaseResponse>>  pipePool)
    {
        _adamPipeRequest = adamPipeRequest;
        _responsesByRequestId = responsesByRequestId;
        _adamPipeResponsePool = pipePool;
    }
    
    public BaseResponse MakeRequest(BaseRequest request, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid();
        var responsePipe = _adamPipeResponsePool.GetObject();
        
        _responsesByRequestId[requestId] = responsePipe;
        
        _adamPipeRequest.SendSignal(new RequestContext
        {
            RequestId = requestId,
            Request = request
        });
        
        var response = responsePipe.Wait(ct);
        
        _responsesByRequestId.TryRemove(requestId, out _);
        _adamPipeResponsePool.Return(responsePipe);
        
        return response;
    }
}