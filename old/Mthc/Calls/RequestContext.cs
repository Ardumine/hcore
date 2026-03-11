using HCore.Main.Mthc.Calls.Requests;
using HCore.Main.Mthc.Calls.Responses;

namespace HCore.Main.Mthc.Calls;

public class RequestContext
{
    public required Guid RequestId { get; set; }
    public required BaseRequest Request { get; set; }
}

public class ResponseContext
{
    public required Guid RequestId { get; set; }
    public required BaseResponse Response { get; set; }
}