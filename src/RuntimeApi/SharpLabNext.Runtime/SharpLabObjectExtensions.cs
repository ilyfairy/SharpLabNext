using System.ComponentModel;
using SharpLab.Runtime;

public static class SharpLabObjectExtensions
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static T Dump<T>(this T value)
    {
        RuntimeServices.Write(new InspectionRecord(InspectionKind.Value, "Dump", value));
        return value;
    }

    public static void Inspect<T>(this T value, string? title = null)
    {
        RuntimeServices.Write(new InspectionRecord(InspectionKind.Value, title ?? "Inspect", value));
    }

    public static void Inspect<T>(this Span<T> value, string? title = null)
    {
        ((ReadOnlySpan<T>)value).Inspect(title);
    }

    public static void Inspect<T>(this ReadOnlySpan<T> value, string? title = null)
    {
        RuntimeServices.Write(new InspectionRecord(InspectionKind.Value, title ?? "Inspect", value.ToArray()));
    }
}
