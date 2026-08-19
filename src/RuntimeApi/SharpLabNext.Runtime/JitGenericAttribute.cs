namespace SharpLab.Runtime;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public class JitGenericAttribute(params Type[] argumentTypes) : Attribute
{
    public Type[] ArgumentTypes { get; } = argumentTypes ?? throw new ArgumentNullException(nameof(argumentTypes));
}
