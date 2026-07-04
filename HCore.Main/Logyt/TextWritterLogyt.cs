namespace Logyt;

public abstract class TextWritterLogyt : Logyt
{
    private TextWriter _normalWriter;
    private TextWriter _errorWriter;


    public TextWritterLogyt(TextWriter normalOutputWritter, string description = "") : this(normalOutputWritter,
        normalOutputWritter, description)
    {
    }

    public TextWritterLogyt(TextWriter normalOutputWritter, TextWriter errorWriter, string description = "")
    {
        _normalWriter = normalOutputWritter;
        _errorWriter = errorWriter;
        Description = description;
    }

    public override void I(string message)
    {
        Log(MessageType.Info, message);
    }

    public override void W(string message)
    {
        Log(MessageType.Warning, message);
    }

    public virtual void Log(MessageType messageType, string message)
    {
        if (messageType is MessageType.Error or MessageType.Critical)
        {
            _errorWriter.WriteLine(GenerateLine(messageType, message));
        }
        else
        {
            _normalWriter.WriteLine(GenerateLine(messageType, message));
        }
    }

    public string GenerateLine(MessageType messageType, string message)
    {
        string str =
            $"{MessageTypeEx.GetAsLetter(messageType)}[{GenerateTimeStampString()} {Description}] {message}";
        return str;
    }
}
