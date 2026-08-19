using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace SharpLabNext.LegacyJitInspector
{
    internal sealed class UserAssemblyLoader : IDisposable
    {
        private readonly string _assemblyPath;
        private readonly string _artifactDirectory;
        private readonly string _helperDirectory;

        public UserAssemblyLoader(string assemblyPath)
        {
            _assemblyPath = assemblyPath ?? throw new ArgumentNullException(nameof(assemblyPath));
            _artifactDirectory = Path.GetDirectoryName(assemblyPath)
                ?? throw new ArgumentException("The entry assembly has no parent directory.", nameof(assemblyPath));
            _helperDirectory = AppContext.BaseDirectory;
            AssemblyLoadContext.Default.Resolving += Resolve;
        }

        public Assembly Load()
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(_assemblyPath);
        }

        public void Dispose()
        {
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }

        private Assembly Resolve(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName.Name) || !IsSimpleFileName(assemblyName.Name))
                return null;

            string fileName = assemblyName.Name + ".dll";
            string artifactCandidate = Path.Combine(_artifactDirectory, fileName);
            if (File.Exists(artifactCandidate))
                return context.LoadFromAssemblyPath(artifactCandidate);

            string helperCandidate = Path.Combine(_helperDirectory, fileName);
            return File.Exists(helperCandidate) ? context.LoadFromAssemblyPath(helperCandidate) : null;
        }

        private static bool IsSimpleFileName(string name)
        {
            return name != "." &&
                name != ".." &&
                name.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                name.IndexOf(Path.AltDirectorySeparatorChar) < 0 &&
                name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
    }
}
