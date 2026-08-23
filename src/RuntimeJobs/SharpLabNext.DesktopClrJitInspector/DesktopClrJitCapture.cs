using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SharpLabNext.DesktopClrJitInspector
{
    internal sealed class DesktopClrJitCaptureDocument
    {
        public DesktopClrJitCaptureDocument(
            string runtimeVersion,
            Guid moduleVersionId,
            IList<DesktopClrJitCaptureMethod> methods)
        {
            if (runtimeVersion == null)
                throw new ArgumentNullException(nameof(runtimeVersion));
            if (methods == null)
                throw new ArgumentNullException(nameof(methods));
            RuntimeVersion = runtimeVersion;
            ModuleVersionId = moduleVersionId;
            Methods = methods;
        }

        public string RuntimeVersion { get; private set; }

        public Guid ModuleVersionId { get; private set; }

        public IList<DesktopClrJitCaptureMethod> Methods { get; private set; }
    }

    internal sealed class DesktopClrJitCaptureMethod
    {
        public DesktopClrJitCaptureMethod(int metadataToken, string displayIdentity, ulong nativeAddress, byte[] nativeCode)
        {
            if (nativeCode == null)
                throw new ArgumentNullException(nameof(nativeCode));
            MetadataToken = metadataToken;
            DisplayIdentity = displayIdentity;
            NativeAddress = nativeAddress;
            NativeCode = nativeCode;
        }

        public int MetadataToken { get; private set; }

        public string DisplayIdentity { get; private set; }

        public ulong NativeAddress { get; private set; }

        public byte[] NativeCode { get; private set; }
    }

    internal static class DesktopClrJitCaptureCodec
    {
        internal const int MaximumMethods = 512;
        internal const int MaximumMethodBytes = 1024 * 1024;
        internal const int MaximumTotalBytes = 8 * 1024 * 1024;
        internal const int MaximumIdentityBytes = 1024;
        internal const int MaximumRuntimeVersionBytes = 64;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SLNDCJ01");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static void Write(Stream destination, DesktopClrJitCaptureDocument document)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (!destination.CanWrite)
                throw new ArgumentException("The capture destination is not writable.", nameof(destination));

            var methods = document.Methods;
            if (methods.Count > MaximumMethods)
                throw new InvalidDataException("The capture contains too many methods.");

            var tokens = new Dictionary<int, bool>();
            var ranges = new Dictionary<string, bool>(StringComparer.Ordinal);
            int totalBytes = 0;
            var encodedIdentities = new List<byte[]>(methods.Count);
            byte[] runtimeVersion = EncodeRuntimeVersion(document.RuntimeVersion);
            for (int index = 0; index < methods.Count; index++)
            {
                DesktopClrJitCaptureMethod method = methods[index];
                if (method == null)
                    throw new InvalidDataException("The capture contains a null method.");
                ValidateMethod(method, tokens, ranges, ref totalBytes, out byte[] identity);
                encodedIdentities.Add(identity);
            }

            WriteBytes(destination, Magic);
            WriteInt32(destination, 1);
            WriteInt32(destination, methods.Count);
            WriteInt32(destination, totalBytes);
            WriteBytes(destination, document.ModuleVersionId.ToByteArray());
            WriteUInt16(destination, (ushort)runtimeVersion.Length);
            WriteBytes(destination, runtimeVersion);
            for (int index = 0; index < methods.Count; index++)
            {
                DesktopClrJitCaptureMethod method = methods[index];
                byte[] identity = encodedIdentities[index];
                WriteInt32(destination, method.MetadataToken);
                WriteUInt64(destination, method.NativeAddress);
                WriteInt32(destination, method.NativeCode.Length);
                WriteUInt16(destination, (ushort)identity.Length);
                WriteBytes(destination, identity);
                WriteBytes(destination, method.NativeCode);
            }
        }

        public static DesktopClrJitCaptureDocument Read(Stream source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (!source.CanRead)
                throw new ArgumentException("The capture source is not readable.", nameof(source));

            ReadExact(source, Magic.Length, "capture magic", out byte[] magic);
            for (int index = 0; index < Magic.Length; index++)
            {
                if (magic[index] != Magic[index])
                    throw new InvalidDataException("The capture magic is invalid.");
            }

            int version = ReadInt32(source, "capture version");
            if (version != 1)
                throw new InvalidDataException("The capture version is not supported.");
            int count = ReadInt32(source, "capture method count");
            int declaredTotalBytes = ReadInt32(source, "capture total bytes");
            if (count < 0 || count > MaximumMethods || declaredTotalBytes < 0 || declaredTotalBytes > MaximumTotalBytes)
                throw new InvalidDataException("The capture header exceeds a limit.");
            ReadExact(source, 16, "capture module MVID", out byte[] mvidBytes);
            int runtimeVersionLength = ReadUInt16(source, "capture runtime version length");
            if (runtimeVersionLength <= 0 || runtimeVersionLength > MaximumRuntimeVersionBytes)
                throw new InvalidDataException("The capture runtime version exceeds a limit.");
            ReadExact(source, runtimeVersionLength, "capture runtime version", out byte[] runtimeVersionBytes);
            string runtimeVersion;
            try
            {
                runtimeVersion = Utf8.GetString(runtimeVersionBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The capture runtime version is not UTF-8.", exception);
            }
            ValidateRuntimeVersion(runtimeVersion);

            var methods = new List<DesktopClrJitCaptureMethod>(count);
            var tokens = new Dictionary<int, bool>();
            var ranges = new Dictionary<string, bool>(StringComparer.Ordinal);
            int totalBytes = 0;
            for (int index = 0; index < count; index++)
            {
                int token = ReadInt32(source, "method metadata token");
                ulong nativeAddress = ReadUInt64(source, "method native address");
                int codeLength = ReadInt32(source, "method native code length");
                int identityLength = ReadUInt16(source, "method display identity length");
                if (identityLength <= 0 || identityLength > MaximumIdentityBytes ||
                    codeLength <= 0 || codeLength > MaximumMethodBytes)
                {
                    throw new InvalidDataException("A capture method exceeds a limit.");
                }
                ReadExact(source, identityLength, "method display identity", out byte[] identityBytes);
                string identity;
                try
                {
                    identity = Utf8.GetString(identityBytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException("A capture method identity is not UTF-8.", exception);
                }
                ReadExact(source, codeLength, "method native code", out byte[] nativeCode);
                var method = new DesktopClrJitCaptureMethod(token, identity, nativeAddress, nativeCode);
                ValidateMethod(method, tokens, ranges, ref totalBytes, out _);
                methods.Add(method);
            }
            if (totalBytes != declaredTotalBytes || source.ReadByte() != -1)
                throw new InvalidDataException("The capture size or trailing bytes are invalid.");

            return new DesktopClrJitCaptureDocument(runtimeVersion, new Guid(mvidBytes), methods);
        }

        private static void ValidateMethod(
            DesktopClrJitCaptureMethod method,
            Dictionary<int, bool> tokens,
            Dictionary<string, bool> ranges,
            ref int totalBytes,
            out byte[] identityBytes)
        {
            if ((method.MetadataToken & unchecked((int)0xff000000)) != 0x06000000)
                throw new InvalidDataException("A capture method metadata token is not a MethodDef token.");
            if (method.NativeAddress == 0 || method.NativeCode.Length <= 0 || method.NativeCode.Length > MaximumMethodBytes)
                throw new InvalidDataException("A capture method native range is invalid.");
            if (method.NativeAddress > ulong.MaxValue - (ulong)method.NativeCode.Length)
                throw new InvalidDataException("A capture method native range overflows.");
            if (tokens.ContainsKey(method.MetadataToken))
                throw new InvalidDataException("The capture contains a duplicate method metadata token.");
            tokens.Add(method.MetadataToken, true);
            string range = method.NativeAddress.ToString("x16", CultureInfo.InvariantCulture) + "+" +
                method.NativeCode.Length.ToString(CultureInfo.InvariantCulture);
            if (ranges.ContainsKey(range))
                throw new InvalidDataException("The capture contains a duplicate native range.");
            ranges.Add(range, true);
            identityBytes = EncodeIdentity(method.DisplayIdentity);
            if (totalBytes > MaximumTotalBytes - method.NativeCode.Length)
                throw new InvalidDataException("The capture native code exceeds the total limit.");
            totalBytes += method.NativeCode.Length;
        }

        internal static byte[] EncodeIdentity(string identity)
        {
            if (string.IsNullOrEmpty(identity) || identity.Length > MaximumIdentityBytes)
                throw new InvalidDataException("A capture method identity is invalid.");
            for (int index = 0; index < identity.Length; index++)
            {
                char value = identity[index];
                if (value < ' ' || value == '\u007f')
                    throw new InvalidDataException("A capture method identity contains a control character.");
                if (char.IsHighSurrogate(value))
                {
                    if (++index >= identity.Length || !char.IsLowSurrogate(identity[index]))
                        throw new InvalidDataException("A capture method identity contains an unpaired surrogate.");
                }
                else if (char.IsLowSurrogate(value))
                {
                    throw new InvalidDataException("A capture method identity contains an unpaired surrogate.");
                }
            }
            byte[] bytes = Utf8.GetBytes(identity);
            if (bytes.Length == 0 || bytes.Length > MaximumIdentityBytes)
                throw new InvalidDataException("A capture method identity exceeds the byte limit.");
            return bytes;
        }

        private static byte[] EncodeRuntimeVersion(string runtimeVersion)
        {
            ValidateRuntimeVersion(runtimeVersion);
            byte[] bytes = Utf8.GetBytes(runtimeVersion);
            if (bytes.Length == 0 || bytes.Length > MaximumRuntimeVersionBytes)
                throw new InvalidDataException("The capture runtime version exceeds the byte limit.");
            return bytes;
        }

        private static void ValidateRuntimeVersion(string runtimeVersion)
        {
            if (string.IsNullOrEmpty(runtimeVersion) || runtimeVersion.Length > MaximumRuntimeVersionBytes)
                throw new InvalidDataException("The capture runtime version is invalid.");
            try
            {
                Version parsed = new Version(runtimeVersion);
                if (parsed.Major < 0)
                    throw new InvalidDataException("The capture runtime version is invalid.");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The capture runtime version is invalid.", exception);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The capture runtime version is invalid.", exception);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("The capture runtime version is invalid.", exception);
            }
            for (int index = 0; index < runtimeVersion.Length; index++)
            {
                char value = runtimeVersion[index];
                if ((value < '0' || value > '9') && value != '.')
                    throw new InvalidDataException("The capture runtime version is not canonical.");
            }
        }

        private static int ReadInt32(Stream source, string label)
        {
            ReadExact(source, 4, label, out byte[] bytes);
            return bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24;
        }

        private static ushort ReadUInt16(Stream source, string label)
        {
            ReadExact(source, 2, label, out byte[] bytes);
            return (ushort)(bytes[0] | bytes[1] << 8);
        }

        private static ulong ReadUInt64(Stream source, string label)
        {
            ReadExact(source, 8, label, out byte[] bytes);
            ulong value = 0;
            for (int index = 0; index < bytes.Length; index++)
                value |= (ulong)bytes[index] << (index * 8);
            return value;
        }

        private static void ReadExact(Stream source, int count, string label, out byte[] bytes)
        {
            bytes = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = source.Read(bytes, offset, count - offset);
                if (read <= 0)
                    throw new InvalidDataException("The " + label + " is truncated.");
                offset += read;
            }
        }

        private static void WriteInt32(Stream destination, int value)
        {
            unchecked
            {
                destination.WriteByte((byte)value);
                destination.WriteByte((byte)(value >> 8));
                destination.WriteByte((byte)(value >> 16));
                destination.WriteByte((byte)(value >> 24));
            }
        }

        private static void WriteUInt16(Stream destination, ushort value)
        {
            destination.WriteByte((byte)value);
            destination.WriteByte((byte)(value >> 8));
        }

        private static void WriteUInt64(Stream destination, ulong value)
        {
            for (int index = 0; index < 8; index++)
                destination.WriteByte((byte)(value >> (index * 8)));
        }

        private static void WriteBytes(Stream destination, byte[] bytes)
        {
            destination.Write(bytes, 0, bytes.Length);
        }
    }
}
