using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SharpLabNext.TargetRuntimeRunner
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            using (Stream frameStream = FrameOutput.Open())
            using (var writer = new RuntimeFrameWriter(frameStream))
            {
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                var started = Stopwatch.StartNew();
                RunOutputCapture capture = null;
                try
                {
                    if (args.Length == 1 && string.Equals(args[0], "self-test", StringComparison.Ordinal))
                    {
                        UserAssemblyRunner.RunSelfTest();
                        writer.Write(
                            RuntimeFrameKind.Exit,
                            JsonPayloadWriter.WriteExit("completed", 0, started.Elapsed.TotalMilliseconds));
                        return 0;
                    }

                    var options = RunArguments.Parse(args);
                    capture = RunOutputCapture.Start(writer);
                    ConfigureStandardInput();
                    int exitCode = UserAssemblyRunner.Run(options.AssemblyPath, options.UserArguments);
                    capture.Emit();
                    writer.Write(
                        RuntimeFrameKind.Exit,
                        JsonPayloadWriter.WriteExit(
                            exitCode == 0 ? "completed" : "non-zero-exit",
                            exitCode,
                            started.Elapsed.TotalMilliseconds));
                    return exitCode;
                }
                catch (OutOfMemoryException)
                {
                    TryEmit(capture);
                    writer.Write(
                        RuntimeFrameKind.Exit,
                        JsonPayloadWriter.WriteExit("out-of-memory", 137, started.Elapsed.TotalMilliseconds));
                    return 137;
                }
                catch (Exception exception)
                {
                    TryEmit(capture);
                    writer.Write(
                        RuntimeFrameKind.Exception,
                        JsonPayloadWriter.WriteException(
                            ExceptionUnwrapper.Unwrap(exception),
                            started.Elapsed.TotalMilliseconds));
                    writer.Write(
                        RuntimeFrameKind.Exit,
                        JsonPayloadWriter.WriteExit("user-exception", 1, started.Elapsed.TotalMilliseconds));
                    return 1;
                }
                finally
                {
                    if (capture != null)
                        capture.Dispose();
                }
            }
        }

        private static void TryEmit(RunOutputCapture capture)
        {
            if (capture == null)
                return;
            try
            {
                capture.Emit();
            }
            catch
            {
                // Preserve the original user exception if output draining fails.
            }
        }

        private static void ConfigureStandardInput()
        {
            string path = Environment.GetEnvironmentVariable("SHARPLABNEXT_STDIN_PATH");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            Console.SetIn(new StreamReader(path, Encoding.UTF8, true));
        }
    }
}
