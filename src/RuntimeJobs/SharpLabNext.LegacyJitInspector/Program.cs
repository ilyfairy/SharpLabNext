using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SharpLabNext.LegacyJitInspector
{
    internal static class Program
    {
        private const int JitFrameChunkSize = 64 * 1024;

        public static int Main(string[] args)
        {
            using (Stream frameStream = FrameOutput.Open())
            using (var writer = new RuntimeFrameWriter(frameStream))
            {
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                var started = Stopwatch.StartNew();
                try
                {
                    string expectedRuntimeVersion;
                    string[] effectiveArgs = RuntimeVersionGuard.Extract(args ?? Array.Empty<string>(), out expectedRuntimeVersion);
                    RuntimeVersionGuard.Validate(expectedRuntimeVersion);

                    if (effectiveArgs.Length > 0 && string.Equals(effectiveArgs[0], "run", StringComparison.Ordinal))
                    {
                        return RunUserAssembly(effectiveArgs, writer, started);
                    }

                    string[] jitArguments = effectiveArgs;
                    if (effectiveArgs.Length > 0 && string.Equals(effectiveArgs[0], "jit", StringComparison.Ordinal))
                    {
                        jitArguments = new string[effectiveArgs.Length - 1];
                        Array.Copy(effectiveArgs, 1, jitArguments, 0, jitArguments.Length);
                    }
                    var options = LegacyJitInspectorArguments.Parse(jitArguments);
                    WindowsJitOutputRedirector.RedirectIfNeeded();
                    using (var loader = new UserAssemblyLoader(options.AssemblyPath))
                    {
                        Assembly assembly = loader.Load();
                        var methods = JitMethodInspector.Inspect(assembly, options.MethodFilter);
                        NativeStreamFlusher.FlushAll();
                        string rawAssembly = JitDisassemblyDocument.ReadOutput();
                        var sourceSpans = PortablePdbMethodMap.Load(options.AssemblyPath);
                        string assemblyText = JitDisassemblyDocument.SelectPreparedMethods(rawAssembly, methods, sourceSpans);

                        WriteChunks(writer, RuntimeFrameKind.JitAssembly, Encoding.UTF8.GetBytes(assemblyText));
                        writer.Write(RuntimeFrameKind.JitSummary, JsonPayloadWriter.WriteJitSummary(Environment.Version.ToString(), assembly.GetName().Name, options.MethodFilter, methods));

                        bool preparedAny = methods.Any(method => method.Status == "prepared");
                        int exitCode = preparedAny && assemblyText.Length > 0 ? 0 : preparedAny ? 1 : 2;
                        writer.Write(RuntimeFrameKind.Exit, JsonPayloadWriter.WriteExit(exitCode == 0 ? "completed" : exitCode == 2 ? "no-matching-methods" : "inspection-failed", exitCode, started.Elapsed.TotalMilliseconds));
                        return exitCode;
                    }
                }
                catch (OutOfMemoryException)
                {
                    writer.Write(RuntimeFrameKind.Exit, JsonPayloadWriter.WriteExit("out-of-memory", 137, started.Elapsed.TotalMilliseconds));
                    return 137;
                }
                catch (Exception exception)
                {
                    writer.Write(RuntimeFrameKind.Exception, JsonPayloadWriter.WriteException(exception, started.Elapsed.TotalMilliseconds));
                    writer.Write(RuntimeFrameKind.Exit, JsonPayloadWriter.WriteExit("inspection-failed", 1, started.Elapsed.TotalMilliseconds));
                    return 1;
                }
            }
        }

        private static int RunUserAssembly(string[] args, RuntimeFrameWriter writer, Stopwatch started)
        {
            var options = RunArguments.Parse(args);
            RunOutputCapture capture = null;
            try
            {
                capture = RunOutputCapture.Start(writer);
                using (var loader = new UserAssemblyLoader(options.AssemblyPath))
                {
                    Assembly assembly = loader.Load();
                    ConfigureStandardInput();
                    int exitCode = UserEntryPointRunner.Run(assembly, options.UserArguments);
                    capture.Emit(writer);
                    writer.Write(RuntimeFrameKind.Exit, JsonPayloadWriter.WriteExit(exitCode == 0 ? "completed" : "non-zero-exit", exitCode, started.Elapsed.TotalMilliseconds));
                    return exitCode;
                }
            }
            catch (OutOfMemoryException)
            {
                TryEmit(capture, writer);
                writer.Write(RuntimeFrameKind.Exit, JsonPayloadWriter.WriteExit("out-of-memory", 137, started.Elapsed.TotalMilliseconds));
                return 137;
            }
            catch (Exception exception)
            {
                TryEmit(capture, writer);
                writer.Write(RuntimeFrameKind.Exception, JsonPayloadWriter.WriteException(exception, started.Elapsed.TotalMilliseconds));
                writer.Write(RuntimeFrameKind.Exit, JsonPayloadWriter.WriteExit("user-exception", 1, started.Elapsed.TotalMilliseconds));
                return 1;
            }
            finally
            {
                if (capture != null)
                    capture.Dispose();
            }
        }

        private static void TryEmit(RunOutputCapture capture, RuntimeFrameWriter writer)
        {
            if (capture == null)
                return;
            try
            {
                capture.Emit(writer);
            }
            catch
            {
                // The structured exception below is more useful than a second
                // protocol failure while draining a damaged output file.
            }
        }

        private static void ConfigureStandardInput()
        {
            string path = Environment.GetEnvironmentVariable("SHARPLABNEXT_STDIN_PATH");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            Console.SetIn(new StreamReader(path, Encoding.UTF8, true));
        }

        private static void WriteChunks(RuntimeFrameWriter writer, RuntimeFrameKind kind, byte[] content)
        {
            for (int offset = 0; offset < content.Length; offset += JitFrameChunkSize)
            {
                int length = Math.Min(JitFrameChunkSize, content.Length - offset);
                writer.Write(kind, content, offset, length);
            }
        }

    }
}
