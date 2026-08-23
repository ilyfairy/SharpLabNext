using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;

namespace SharpLabNext.TargetRuntimeRunner
{
    internal static class UserAssemblyRunner
    {
        private static readonly char[] WindowsArgumentQuoteCharacters = { ' ', '\t', '\n', '\v', '"' };
        private static readonly string[] SelfTestArguments = { "first", "second" };
        private static readonly string[] SelfTestThrowArguments = { "throw" };
        private static bool _selfTestVoidCalled;
        private const int CorFixupsInExecutable = unchecked((int)0x80131019);
        private delegate object EntryPointInvoker(string[] arguments);
        private delegate object InstanceMethodInvoker(object instance);

        public static int Run(string assemblyPath, string[] arguments)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(assemblyPath);
            }
            catch (System.IO.FileLoadException exception) when (
                Environment.OSVersion.Platform == PlatformID.Win32NT &&
                System.Runtime.InteropServices.Marshal.GetHRForException(exception) == CorFixupsInExecutable)
            {
                return RunNativeEntryPoint(assemblyPath, arguments);
            }
            MethodInfo entryPoint = assembly.EntryPoint;
            if (entryPoint == null)
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    return RunNativeEntryPoint(assemblyPath, arguments);
                throw new InvalidOperationException("The user assembly does not define an entry point.");
            }

            ParameterInfo[] parameters = entryPoint.GetParameters();
            if (!entryPoint.IsStatic)
                throw new InvalidOperationException("The user entry point must be static.");
            string[] invocationArguments;
            if (parameters.Length == 0)
            {
                invocationArguments = null;
            }
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string[]))
            {
                invocationArguments = arguments ?? new string[0];
            }
            else
            {
                throw new InvalidOperationException(
                    "The user entry point must take no parameters or a string[] parameter.");
            }

            Type returnType = entryPoint.ReturnType;
            if (returnType != typeof(void) && returnType != typeof(int) && !IsTask(returnType))
            {
                throw new InvalidOperationException(
                    "Unsupported entry point return type '" + returnType.FullName + "'.");
            }

            object result = CreateEntryPointInvoker(entryPoint)(invocationArguments);
            return CompleteResult(result);
        }

        private static EntryPointInvoker CreateEntryPointInvoker(MethodInfo entryPoint)
        {
            var method = new DynamicMethod(
                "SharpLabNext_InvokeUserEntryPoint",
                typeof(object),
                new Type[] { typeof(string[]) },
                typeof(UserAssemblyRunner),
                true);
            ILGenerator il = method.GetILGenerator();
            if (entryPoint.GetParameters().Length == 1)
                il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, entryPoint);
            EmitObjectReturn(il, entryPoint.ReturnType);
            return (EntryPointInvoker)method.CreateDelegate(typeof(EntryPointInvoker));
        }

        private static int RunNativeEntryPoint(string assemblyPath, string[] arguments)
        {
            var startInfo = new ProcessStartInfo(assemblyPath, BuildWindowsCommandLine(arguments))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new InvalidOperationException("The native user entry point could not be started.");
                Exception stdoutFailure = null;
                Exception stderrFailure = null;
                var stdoutThread = StartOutputPump(
                    "SharpLabNext.NativeEntryPoint.Stdout",
                    process.StandardOutput,
                    Console.Out,
                    delegate(Exception exception) { stdoutFailure = exception; });
                var stderrThread = StartOutputPump(
                    "SharpLabNext.NativeEntryPoint.Stderr",
                    process.StandardError,
                    Console.Error,
                    delegate(Exception exception) { stderrFailure = exception; });
                process.WaitForExit();
                stdoutThread.Join();
                stderrThread.Join();
                if (stdoutFailure != null)
                    throw new IOException("The native user stdout stream could not be captured.", stdoutFailure);
                if (stderrFailure != null)
                    throw new IOException("The native user stderr stream could not be captured.", stderrFailure);
                return process.ExitCode;
            }
        }

        private static Thread StartOutputPump(
            string name,
            StreamReader reader,
            TextWriter writer,
            Action<Exception> reportFailure)
        {
            var thread = new Thread(delegate()
            {
                try
                {
                    var buffer = new char[4096];
                    int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                        writer.Write(buffer, 0, count);
                    writer.Flush();
                }
                catch (Exception exception)
                {
                    reportFailure(exception);
                }
            });
            thread.IsBackground = true;
            thread.Name = name;
            thread.Start();
            return thread;
        }

        private static string BuildWindowsCommandLine(string[] arguments)
        {
            if (arguments == null || arguments.Length == 0)
                return string.Empty;

            var commandLine = new StringBuilder();
            for (int index = 0; index < arguments.Length; index++)
            {
                if (index > 0)
                    commandLine.Append(' ');
                AppendWindowsArgument(commandLine, arguments[index] ?? string.Empty);
            }
            return commandLine.ToString();
        }

        private static void AppendWindowsArgument(StringBuilder builder, string argument)
        {
            bool requiresQuotes = argument.Length == 0 ||
                argument.IndexOfAny(WindowsArgumentQuoteCharacters) >= 0;
            if (!requiresQuotes)
            {
                builder.Append(argument);
                return;
            }

            builder.Append('"');
            int backslashes = 0;
            for (int index = 0; index < argument.Length; index++)
            {
                char character = argument[index];
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(character);
            }
            builder.Append('\\', backslashes * 2);
            builder.Append('"');
        }

        private static int CompleteResult(object result)
        {
            if (result == null)
                return 0;
            if (result is int)
                return (int)result;
            if (!IsTask(result.GetType()))
            {
                throw new InvalidOperationException(
                    "Unsupported entry point return type '" + result.GetType().FullName + "'.");
            }

            MethodInfo getAwaiter = result.GetType().GetMethod(
                "GetAwaiter",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            object completedResult;
            if (getAwaiter != null)
            {
                object awaiter = InvokeInstanceMethod(result, getAwaiter);
                if (awaiter == null)
                    throw new InvalidOperationException("The Task returned a null awaiter.");
                MethodInfo getResult = awaiter.GetType().GetMethod(
                    "GetResult",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (getResult == null)
                    throw new InvalidOperationException("The Task awaiter does not expose GetResult().");
                completedResult = InvokeInstanceMethod(awaiter, getResult);
            }
            else
            {
                MethodInfo wait = result.GetType().GetMethod(
                    "Wait",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (wait == null)
                    throw new InvalidOperationException("The Task does not expose Wait().");
                InvokeInstanceMethod(result, wait);
                PropertyInfo resultProperty = result.GetType().GetProperty(
                    "Result",
                    BindingFlags.Instance | BindingFlags.Public);
                MethodInfo resultGetter = resultProperty == null ? null : resultProperty.GetGetMethod();
                completedResult = resultGetter == null ? null : InvokeInstanceMethod(result, resultGetter);
            }

            if (completedResult == null)
                return 0;
            if (completedResult is int)
                return (int)completedResult;
            throw new InvalidOperationException("The Task entry point result must be Int32.");
        }

        private static object InvokeInstanceMethod(object instance, MethodInfo target)
        {
            Type declaringType = target.DeclaringType;
            if (declaringType == null || target.IsStatic || target.GetParameters().Length != 0)
                throw new InvalidOperationException("The Task completion method shape is invalid.");

            var method = new DynamicMethod(
                "SharpLabNext_InvokeTaskMethod",
                typeof(object),
                new Type[] { typeof(object) },
                typeof(UserAssemblyRunner),
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            if (declaringType.IsValueType)
            {
                il.Emit(OpCodes.Unbox, declaringType);
                il.Emit(OpCodes.Call, target);
            }
            else
            {
                il.Emit(OpCodes.Castclass, declaringType);
                il.Emit(target.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, target);
            }
            EmitObjectReturn(il, target.ReturnType);
            var invoker = (InstanceMethodInvoker)method.CreateDelegate(typeof(InstanceMethodInvoker));
            return invoker(instance);
        }

        private static void EmitObjectReturn(ILGenerator il, Type returnType)
        {
            if (returnType == typeof(void))
                il.Emit(OpCodes.Ldnull);
            else if (returnType.IsValueType)
                il.Emit(OpCodes.Box, returnType);
            il.Emit(OpCodes.Ret);
        }

        internal static void RunSelfTest()
        {
            MethodInfo entryPoint = typeof(UserAssemblyRunner).GetMethod(
                "SelfTestEntryPoint",
                BindingFlags.Static | BindingFlags.NonPublic);
            EntryPointInvoker invoker = CreateEntryPointInvoker(entryPoint);
            if (!object.Equals(invoker(SelfTestArguments), 2))
                throw new InvalidOperationException("Direct entry point invocation self-test failed.");

            try
            {
                invoker(SelfTestThrowArguments);
                throw new InvalidOperationException("Entry point exception self-test did not throw.");
            }
            catch (InvalidOperationException exception)
            {
                if (!string.Equals(exception.Message, "outer entry point self-test", StringComparison.Ordinal) ||
                    exception.InnerException == null ||
                    !string.Equals(
                        exception.InnerException.Message,
                        "inner entry point self-test",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Entry point exception identity self-test failed.");
                }
            }

            MethodInfo noArgumentEntryPoint = typeof(UserAssemblyRunner).GetMethod(
                "SelfTestNoArgumentEntryPoint",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (!object.Equals(CreateEntryPointInvoker(noArgumentEntryPoint)(null), 5))
                throw new InvalidOperationException("No-argument entry point self-test failed.");
            MethodInfo voidEntryPoint = typeof(UserAssemblyRunner).GetMethod(
                "SelfTestVoidEntryPoint",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (CreateEntryPointInvoker(voidEntryPoint)(null) != null || !_selfTestVoidCalled)
                throw new InvalidOperationException("Void entry point self-test failed.");

            object awaiter = new SelfTestAwaiter(7);
            MethodInfo getResult = typeof(SelfTestAwaiter).GetMethod("GetResult");
            if (!object.Equals(InvokeInstanceMethod(awaiter, getResult), 7))
                throw new InvalidOperationException("Value-type awaiter invocation self-test failed.");

            var waitable = new SelfTestWaitable();
            InvokeInstanceMethod(waitable, typeof(SelfTestWaitable).GetMethod("Wait"));
            object waitedResult = InvokeInstanceMethod(
                waitable,
                typeof(SelfTestWaitable).GetProperty("Result").GetGetMethod());
            if (!waitable.Waited || !object.Equals(waitedResult, 11))
                throw new InvalidOperationException("Wait/Result invocation self-test failed.");
        }

        private static int SelfTestEntryPoint(string[] arguments)
        {
            if (arguments.Length == 1 && string.Equals(arguments[0], "throw", StringComparison.Ordinal))
            {
                try
                {
                    throw new ArgumentException("inner entry point self-test");
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidOperationException("outer entry point self-test", exception);
                }
            }
            return arguments.Length;
        }

        private static int SelfTestNoArgumentEntryPoint()
        {
            return 5;
        }

        private static void SelfTestVoidEntryPoint()
        {
            _selfTestVoidCalled = true;
        }

        private struct SelfTestAwaiter
        {
            private readonly int _result;

            public SelfTestAwaiter(int result)
            {
                _result = result;
            }

            public int GetResult()
            {
                return _result;
            }
        }

        private sealed class SelfTestWaitable
        {
            public bool Waited { get; private set; }

            public int Result
            {
                get { return Waited ? 11 : -1; }
            }

            public void Wait()
            {
                Waited = true;
            }
        }

        private static bool IsTask(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                if (string.Equals(
                    current.FullName,
                    "System.Threading.Tasks.Task",
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return type.IsGenericType && string.Equals(
                type.GetGenericTypeDefinition().FullName,
                "System.Threading.Tasks.Task`1",
                StringComparison.Ordinal);
        }
    }

    internal static class ExceptionUnwrapper
    {
        public static Exception Unwrap(Exception exception)
        {
            for (int depth = 0; depth < 32 && exception.InnerException != null; depth++)
            {
                if (exception is TargetInvocationException ||
                    string.Equals(exception.GetType().FullName, "System.AggregateException", StringComparison.Ordinal))
                {
                    exception = exception.InnerException;
                    continue;
                }
                break;
            }
            return exception;
        }
    }
}
