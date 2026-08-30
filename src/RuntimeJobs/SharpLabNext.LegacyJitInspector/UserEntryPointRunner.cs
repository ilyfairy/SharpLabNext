using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLabNext.LegacyJitInspector
{
    internal static class UserEntryPointRunner
    {
        public static int Run(Assembly assembly, string[] arguments)
        {
            if (assembly == null)
                throw new ArgumentNullException(nameof(assembly));
            MethodInfo entryPoint = assembly.EntryPoint ?? throw new InvalidOperationException("The user assembly does not define an entry point.");

            ParameterInfo[] parameters = entryPoint.GetParameters();
            object[] invocationArguments;
            if (parameters.Length == 0)
            {
                invocationArguments = null;
            }
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string[]))
            {
                invocationArguments = new object[] { arguments ?? Array.Empty<string>() };
            }
            else
            {
                throw new InvalidOperationException("The user entry point must take no parameters or a string[] parameter.");
            }

            object result;
            try
            {
                result = entryPoint.Invoke(null, invocationArguments);
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            return AwaitResult(result);
        }

        private static int AwaitResult(object result)
        {
            if (result == null)
                return 0;
            if (result is int)
                return (int)result;
            var integerTask = result as Task<int>;
            if (integerTask != null)
                return integerTask.GetAwaiter().GetResult();
            var task = result as Task;
            if (task != null)
            {
                task.GetAwaiter().GetResult();
                return 0;
            }
            throw new InvalidOperationException("Unsupported entry point return type '" + result.GetType().FullName + "'.");
        }
    }

}
