using System;
using System.IO;

namespace SharpLabNext.LegacyJitInspector
{
    internal sealed class RunArguments
    {
        private RunArguments(string assemblyPath, string[] userArguments)
        {
            AssemblyPath = assemblyPath;
            UserArguments = userArguments;
        }

        public string AssemblyPath { get; private set; }

        public string[] UserArguments { get; private set; }

        public static RunArguments Parse(string[] args)
        {
            if (args == null || args.Length < 3)
                throw new ArgumentException("Usage: SharpLabNext.LegacyJitInspector run <absolute-assembly-path> -- [arguments]", nameof(args));
            if (!string.Equals(args[0], "run", StringComparison.Ordinal))
                throw new ArgumentException("The run command is invalid.", nameof(args));
            if (!string.Equals(args[2], "--", StringComparison.Ordinal))
                throw new ArgumentException("The run command requires a '--' argument separator.", nameof(args));

            string path = Path.GetFullPath(args[1]);
            if (!Path.IsPathRooted(args[1]) || !File.Exists(path))
                throw new FileNotFoundException("User entry assembly was not found.", path);

            int userArgumentCount = args.Length - 3;
            var userArguments = new string[Math.Max(0, userArgumentCount)];
            if (userArgumentCount > 0)
                Array.Copy(args, 3, userArguments, 0, userArgumentCount);
            return new RunArguments(path, userArguments);
        }
    }
}
