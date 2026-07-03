namespace Logyt;

public class ConsoleLogyt: TextWritterLogyt
{
    public ConsoleLogyt(string description = "") : base(Console.Out, Console.Error, description)
    {
    }

  

    public override void Log(MessageType messageType, string message)
    {
        SetColorForMessageType(messageType);   
        base.Log(messageType, message);
        RevertConsoleColor();
    }
    
    
    private ConsoleColor _previousConsoleColor;
    private void SetColorForMessageType(MessageType messageType)
    {
        _previousConsoleColor = Console.ForegroundColor;
        Console.ForegroundColor = messageType switch
        {
            MessageType.Debug    => ConsoleColor.DarkGray,
            MessageType.Info => ConsoleColor.Cyan,
            MessageType.Warning => ConsoleColor.Yellow,
            MessageType.Error => ConsoleColor.Red,
            MessageType.Critical => ConsoleColor.DarkRed,
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null)
        };
    }

    private void RevertConsoleColor()
    {
        Console.ForegroundColor = _previousConsoleColor;
    }
}
