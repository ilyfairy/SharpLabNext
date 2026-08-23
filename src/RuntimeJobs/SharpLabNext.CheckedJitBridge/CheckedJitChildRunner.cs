using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using SharpLabNext.RuntimeJobs;

namespace SharpLabNext.CheckedJitBridge;

internal static class CheckedJitChildRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parsed = CheckedJitChildArguments.Parse(args);
        using var pipe = new AnonymousPipeClientStream(PipeDirection.Out, parsed.PipeHandle);
        PipeHandleInheritance.Disable(pipe.SafePipeHandle);
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        ChildResultEnvelope envelope;
        var exitCode = 0;
        try
        {
            using var loader = new ChildUserAssemblyLoader(parsed.AssemblyPath);
            var assembly = loader.Load();
            var methods = ChildMethodInspector.Inspect(assembly, parsed.MethodFilter);
            ChildNativeStreamFlusher.FlushAll();
            envelope = new ChildResultEnvelope(
                ChildResultEnvelope.ProtocolMagic,
                parsed.Nonce,
                assembly.GetName().Name ?? throw new BadImageFormatException(
                    "User assembly does not define a simple assembly name."),
                methods,
                null);
        }
        catch (OutOfMemoryException exception)
        {
            exitCode = 137;
            envelope = CreateFailureEnvelope(parsed, exception);
        }
        catch (Exception exception)
        {
            exitCode = 1;
            envelope = CreateFailureEnvelope(parsed, exception);
        }

        var payload = ChildResultCodec.Serialize(envelope);
        await pipe.WriteAsync(payload.AsMemory(), default).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
        return exitCode;
    }

    private static ChildResultEnvelope CreateFailureEnvelope(
        CheckedJitChildArguments parsed,
        Exception exception)
    {
        string assemblyName;
        try
        {
            using var metadata = ManagedAssemblyMetadata.Open(parsed.AssemblyPath);
            assemblyName = metadata.AssemblyName;
        }
        catch
        {
            assemblyName = Path.GetFileNameWithoutExtension(parsed.AssemblyPath);
        }

        return new ChildResultEnvelope(
            ChildResultEnvelope.ProtocolMagic,
            parsed.Nonce,
            assemblyName,
            Array.Empty<ChildMethodRecord>(),
            new ChildErrorRecord(
                Bound(exception.GetType().FullName ?? exception.GetType().Name, 4_096),
                Bound(exception.Message, 4_096),
                exception.StackTrace is null ? null : Bound(exception.StackTrace, 4_096)));
    }

    private static string Bound(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
            return value;
        return value.Substring(0, maximumLength);
    }
}
