using System.Collections.Concurrent;
using HCore.Main.Extras;
using HCore.Main.Mthc.Calls;
using HCore.Main.Mthc.Calls.Responses;
using HCore.Modules.Base;

namespace HCore.Main.Mthc;

public class MthcCreator
{
    
    public void CreateForModule(RModule module)
    {
        var adamPipeReceiver = new AdamPipe<RequestContext>();
    
        var r = new ConcurrentDictionary<Guid, AdamPipe<BaseResponse>>();
        
        var callReceiver = new CallReceiver(adamPipeReceiver, r);
        var callClient = new CallClient(adamPipeReceiver, r, new ObjectPool<AdamPipe<BaseResponse>>());
    }

 
}