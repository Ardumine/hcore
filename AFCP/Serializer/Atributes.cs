namespace AFCP;

/// <summary>
/// Marks a property on a serializable class to be skipped by the reflection-free
/// class serializer. Use it for fields that hold kernel-side handles, delegates,
/// or other non-portable references that must never cross the wire.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IgnoreParseAttribute : Attribute
{
}
