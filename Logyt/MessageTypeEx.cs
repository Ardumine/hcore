namespace Logyt;

public static class MessageTypeEx
{
    public static string GetAsLetter(MessageType messageType)
    {
        return messageType switch
        {
            MessageType.Debug => "D",
            MessageType.Info => "I",
            MessageType.Warning => "W",
            MessageType.Error => "E",
            MessageType.Critical => "C",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null)
        };
    }
}