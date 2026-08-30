using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpLabNext.DesktopClrJitInspector
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                DesktopClrJitCaptureArguments options = DesktopClrJitCaptureArguments.Parse(args);
                DesktopClrJitCaptureDocument capture = DesktopClrJitCaptureRunner.Capture(options);
                DesktopClrJitCaptureFile.WriteAtomically(options.OutputPath, capture);
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long WindowsAbi(long left, long right)
        {
            long value = left + right;
            return value > 10 ? value * 3 : value - 1;
        }
    }

    internal sealed class DesktopClrJitCaptureArguments
    {
        internal const string FixedCapturePath = @"Z:\tmp\sharplabnext-desktop-jit.bin";

        private DesktopClrJitCaptureArguments(string assemblyPath, string outputPath, string methodFilter)
        {
            AssemblyPath = assemblyPath;
            OutputPath = outputPath;
            MethodFilter = methodFilter;
        }

        public string AssemblyPath { get; private set; }

        public string OutputPath { get; private set; }

        public string MethodFilter { get; private set; }

        public static DesktopClrJitCaptureArguments Parse(string[] args)
        {
            if (args == null || args.Length < 3 || args.Length > 4 || !string.Equals(args[0], "capture", StringComparison.Ordinal))
            {
                throw new ArgumentException("Usage: SharpLabNext.DesktopClrJitInspector.exe capture <absolute-assembly> <absolute-output> [method-filter]");
            }
            string assemblyPath = ValidateExistingFile(args[1], "assembly");
            string outputPath = ValidateOutputPath(args[2]);
            string filter = args.Length == 4 ? args[3] : null;
            if (filter != null && (filter.Length > 512 || filter.IndexOf('\0') >= 0))
                throw new ArgumentException("The method filter is invalid.");
            return new DesktopClrJitCaptureArguments(assemblyPath, outputPath, filter);
        }

        private static string ValidateExistingFile(string value, string label)
        {
            if (string.IsNullOrEmpty(value) || !Path.IsPathRooted(value))
                throw new ArgumentException("The " + label + " path must be absolute.");
            string fullPath = Path.GetFullPath(value);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The " + label + " file was not found.", fullPath);
            return fullPath;
        }

        private static string ValidateOutputPath(string value)
        {
            if (string.IsNullOrEmpty(value) || !Path.IsPathRooted(value))
                throw new ArgumentException("The output path must be absolute.");
            string fullPath = Path.GetFullPath(value);
            if (!string.Equals(fullPath, FixedCapturePath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output path is not the fixed desktop CLR capture path.");
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory) || string.IsNullOrEmpty(Path.GetFileName(fullPath)))
            {
                throw new ArgumentException("The output path must have an existing parent directory.");
            }
            return fullPath;
        }
    }

    internal static class DesktopClrJitCaptureRunner
    {
        private const BindingFlags DeclaredMethodFlags = BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Static |
            BindingFlags.Instance |
            BindingFlags.DeclaredOnly;
        private const BindingFlags DeclaredInstanceConstructorFlags = BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.DeclaredOnly;
        private const BindingFlags DeclaredStaticConstructorFlags = BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        public static DesktopClrJitCaptureDocument Capture(DesktopClrJitCaptureArguments options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (IntPtr.Size != 8 || !IsWindows())
                throw new PlatformNotSupportedException("Desktop CLR JIT capture requires Windows x64.");

            ResolveEventHandler resolver = delegate(object sender, ResolveEventArgs args)
            {
                return ResolveDependency(options.AssemblyPath, args.Name);
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            try
            {
                Assembly assembly = Assembly.LoadFrom(options.AssemblyPath);
                var methods = new List<DesktopClrJitCaptureMethod>();
                var tokens = new Dictionary<int, bool>();
                var ranges = new Dictionary<string, bool>(StringComparer.Ordinal);
                int totalNativeBytes = 0;
                foreach (Type type in assembly.GetTypes())
                {
                    foreach (MethodBase method in EnumerateDeclaredMethods(type))
                    {
                        if (!CanPrepare(method))
                            continue;
                        string identity = GetDisplayIdentity(type, method);
                        if (!MatchesFilter(identity, options.MethodFilter))
                            continue;
                        DesktopClrJitCaptureCodec.EncodeIdentity(identity);
                        if (methods.Count >= DesktopClrJitCaptureCodec.MaximumMethods)
                            throw new InvalidDataException("The JIT capture method limit was exceeded.");

                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        IntPtr pointer = method.MethodHandle.GetFunctionPointer();
                        DesktopClrNativeCodeRange range = DesktopClrNativeCodeRange.Resolve(pointer);
                        if (tokens.ContainsKey(method.MetadataToken))
                            throw new InvalidDataException("The JIT capture contains a duplicate method token.");
                        tokens.Add(method.MetadataToken, true);
                        string rangeKey = range.Address.ToString("x16", CultureInfo.InvariantCulture) + "+" +
                            range.Length.ToString(CultureInfo.InvariantCulture);
                        if (ranges.ContainsKey(rangeKey))
                            throw new InvalidDataException("The JIT capture contains a duplicate native range.");
                        ranges.Add(rangeKey, true);
                        if (totalNativeBytes > DesktopClrJitCaptureCodec.MaximumTotalBytes - range.Length)
                            throw new InvalidDataException("The JIT capture native code limit was exceeded.");
                        totalNativeBytes += range.Length;
                        byte[] bytes = new byte[range.Length];
                        Marshal.Copy(range.Start, bytes, 0, bytes.Length);
                        methods.Add(new DesktopClrJitCaptureMethod(method.MetadataToken, identity, range.Address, bytes));
                    }
                }
                return new DesktopClrJitCaptureDocument(Environment.Version.ToString(), assembly.ManifestModule.ModuleVersionId, methods);
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            }
        }

        private static IEnumerable<MethodBase> EnumerateDeclaredMethods(Type type)
        {
            foreach (MethodInfo method in type.GetMethods(DeclaredMethodFlags))
                yield return method;
            ConstructorInfo[] instanceConstructors;
            ConstructorInfo[] staticConstructors;
            try
            {
                instanceConstructors = type.GetConstructors(DeclaredInstanceConstructorFlags);
                staticConstructors = type.GetConstructors(DeclaredStaticConstructorFlags);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("Could not enumerate constructors for " + type.FullName + ".", exception);
            }
            foreach (ConstructorInfo constructor in instanceConstructors)
                yield return constructor;
            foreach (ConstructorInfo constructor in staticConstructors)
                yield return constructor;
        }

        private static bool CanPrepare(MethodBase method)
        {
            if (method.IsAbstract || method.ContainsGenericParameters || (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0 || (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                return false;
            }
            try
            {
                return method.GetMethodBody() != null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static Assembly ResolveDependency(string assemblyPath, string requestedName)
        {
            AssemblyName requested;
            try
            {
                requested = new AssemblyName(requestedName);
            }
            catch (Exception)
            {
                return null;
            }
            if (string.IsNullOrEmpty(requested.Name) || requested.Name == "." || requested.Name == ".." || requested.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }
            string directory = Path.GetDirectoryName(assemblyPath);
            string candidate = Path.Combine(directory, requested.Name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }

        private static string GetDisplayIdentity(Type type, MethodBase method)
        {
            string typeName = type.FullName ?? type.Name;
            string methodName = method.IsConstructor
                ? (method.IsStatic ? ".cctor" : ".ctor") : method.Name;
            return typeName + "." + methodName;
        }

        private static bool MatchesFilter(string value, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;
            return filter.IndexOf('*') >= 0 || filter.IndexOf('?') >= 0
                ? MatchesWildcard(filter, value) : value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesWildcard(string pattern, string value)
        {
            int patternIndex = 0;
            int valueIndex = 0;
            int starIndex = -1;
            int starValueIndex = -1;
            while (valueIndex < value.Length)
            {
                if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' || char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
                {
                    patternIndex++;
                    valueIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex++;
                    starValueIndex = valueIndex;
                }
                else if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    valueIndex = ++starValueIndex;
                }
                else
                {
                    return false;
                }
            }
            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                patternIndex++;
            return patternIndex == pattern.Length;
        }

        private static bool IsWindows()
        {
            PlatformID platform = Environment.OSVersion.Platform;
            return platform == PlatformID.Win32NT ||
                platform == PlatformID.Win32Windows ||
                platform == PlatformID.Win32S ||
                platform == PlatformID.WinCE;
        }
    }

    internal sealed class DesktopClrNativeCodeRange
    {
        private DesktopClrNativeCodeRange(IntPtr start, int length)
        {
            Start = start;
            Length = length;
        }

        public IntPtr Start { get; private set; }

        public int Length { get; private set; }

        public ulong Address => unchecked((ulong)Start.ToInt64());

        public static DesktopClrNativeCodeRange Resolve(IntPtr methodPointer)
        {
            if (methodPointer == IntPtr.Zero)
                throw new InvalidDataException("The prepared method has no native address.");
            ulong address = unchecked((ulong)methodPointer.ToInt64());
            ulong imageBase;
            IntPtr entryPointer = RtlLookupFunctionEntry(address, out imageBase, IntPtr.Zero);
            RuntimeFunction entry = entryPointer == IntPtr.Zero
                ? new RuntimeFunction() : (RuntimeFunction)Marshal.PtrToStructure(entryPointer, typeof(RuntimeFunction));
            if (entryPointer == IntPtr.Zero || entry.EndAddress <= entry.BeginAddress || imageBase > ulong.MaxValue - entry.EndAddress)
            {
                throw new InvalidDataException("The prepared method has no valid x64 unwind range.");
            }
            ulong start = imageBase + entry.BeginAddress;
            ulong end = imageBase + entry.EndAddress;
            if (start != address || end <= start || end - start > DesktopClrJitCaptureCodec.MaximumMethodBytes || start > long.MaxValue)
            {
                throw new InvalidDataException("The prepared method points to a trampoline or an invalid native range.");
            }
            return new DesktopClrNativeCodeRange(new IntPtr(unchecked((long)start)), checked((int)(end - start)));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RuntimeFunction
        {
            public uint BeginAddress;
            public uint EndAddress;
            public uint UnwindData;
        }

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);
    }

    internal static class DesktopClrJitCaptureFile
    {
        public static void WriteAtomically(string outputPath, DesktopClrJitCaptureDocument document)
        {
            if (outputPath == null)
                throw new ArgumentNullException(nameof(outputPath));
            string directory = Path.GetDirectoryName(outputPath);
            string temporaryPath = outputPath + ".tmp";
            try
            {
                if (File.Exists(outputPath) || File.Exists(temporaryPath))
                    throw new IOException("The Desktop CLR capture path is not clean.");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    DesktopClrJitCaptureCodec.Write(stream, document);
                    stream.Flush();
                }
                File.Move(temporaryPath, outputPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}
