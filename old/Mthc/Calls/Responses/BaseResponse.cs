namespace HCore.Main.Mthc.Calls.Responses;

public class BaseResponse
{
    public ManualResetEventSlim Signal { get; } = new ManualResetEventSlim(false);
}