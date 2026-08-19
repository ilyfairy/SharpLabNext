using System;
using System.IO;

namespace SharpLabNext.LegacyJitInspector
{
    internal sealed class LegacyJitInspectorArguments
    {
        private LegacyJitInspectorArguments(string assemblyPath, string methodFilter)
        {
            AssemblyPath = assemblyPath;
            MethodFilter = methodFilter;
        }

        public string AssemblyPath { get; private set; }

        public string MethodFilter { get; private set; }

        public static LegacyJitInspectorArguments Parse(string[] args)
        {
            if (args == null)
                throw new ArgumentNullException(nameof(args));
            if (args.Length == 0 || args.Length > 2)
            {
                throw new ArgumentException(
                    "Usage: SharpLabNext.LegacyJitInspector <absolute-assembly-path> [method-filter]",
                    nameof(args));
            }

            string assemblyPath = Path.GetFullPath(args[0]);
            if (!Path.IsPathRooted(args[0]) || !File.Exists(assemblyPath))
                throw new FileNotFoundException("User entry assembly was not found.", assemblyPath);

            string filter = args.Length == 2 ? args[1] : null;
            if (filter != null && filter.Length > 256)
                throw new ArgumentException("Method filter exceeds 256 characters.", nameof(args));
            if (string.IsNullOrWhiteSpace(filter))
                filter = null;

            return new LegacyJitInspectorArguments(assemblyPath, filter);
        }
    }
}
