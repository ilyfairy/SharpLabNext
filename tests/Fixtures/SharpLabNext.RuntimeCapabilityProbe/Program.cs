using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SharpLabNext.RuntimeCapabilityProbe
{
    public static class Program
    {
        private const string StdoutMarker = "SLN-CAPABILITY-STDOUT-V1";
        private const string StderrMarker = "SLN-CAPABILITY-STDERR-V1";
        private const string NetworkBlockedMarker = "SLN-CAPABILITY-NETWORK-BLOCKED-V1";
        private const string ReadOnlyBlockedMarker = "SLN-CAPABILITY-ROOTFS-READONLY-V1";

        public static int Main(string[] args)
        {
            string mode = args.Length == 0 ? "success-security" : args[0];
            switch (mode)
            {
                case "success-security":
                    return RunSecurityProbe();
                case "user-exception":
                    ThrowNestedException();
                    return 1;
                case "output-overflow":
                    WritePastOutputLimit();
                    return 1;
                case "hang":
                    Thread.Sleep(Timeout.Infinite);
                    return 1;
                case "process-tree":
                    StartLongRunningChild();
                    return 0;
                default:
                    Console.Error.Write("unknown capability probe mode");
                    return 2;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int MultipleSequencePoints(int value)
        {
            int adjusted = value + 1;
            if (adjusted > 10)
                adjusted *= 2;
            else
                adjusted -= 3;
            return adjusted;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long WindowsAbi(long first, long second)
        {
            return first + second;
        }

        private static int RunSecurityProbe()
        {
            Console.WriteLine(StdoutMarker);
            Console.Error.WriteLine(StderrMarker);
            if (NetworkIsBlocked())
                Console.WriteLine(NetworkBlockedMarker);
            if (RootFileSystemIsReadOnly())
                Console.WriteLine(ReadOnlyBlockedMarker);
            return MultipleSequencePoints(12) == 26 && WindowsAbi(20, 22) == 42 ? 0 : 3;
        }

        private static bool NetworkIsBlocked()
        {
            Socket socket = null;
            IAsyncResult connect = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                connect = socket.BeginConnect(IPAddress.Parse("1.1.1.1"), 53, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(500, false))
                    return true;
                socket.EndConnect(connect);
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
            finally
            {
                if (connect != null)
                    connect.AsyncWaitHandle.Close();
                if (socket != null)
                    socket.Close();
            }
        }

        private static bool RootFileSystemIsReadOnly()
        {
            string path = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? @"Z:\sharplabnext-runtime-capability-probe.tmp"
                : "/sharplabnext-runtime-capability-probe.tmp";
            try
            {
                File.WriteAllText(path, "write must fail");
                File.Delete(path);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private static void ThrowNestedException()
        {
            try
            {
                throw new ArgumentException("inner capability probe failure");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException("outer capability probe failure", exception);
            }
        }

        private static void WritePastOutputLimit()
        {
            string chunk = new string('x', 64 * 1024);
            for (int index = 0; index < 128; index++)
            {
                Console.Out.Write(chunk);
                Console.Out.Flush();
            }
            Thread.Sleep(Timeout.Infinite);
        }

        private static void StartLongRunningChild()
        {
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var startInfo = new ProcessStartInfo
            {
                FileName = windows ? "cmd.exe" : "/bin/sh",
                Arguments = windows
                    ? "/d /s /c \"ping -t 127.0.0.1 >NUL 2>NUL\""
                    : "-c \"while :; do sleep 60; done >/dev/null 2>&1\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process child = Process.Start(startInfo);
            if (child == null)
                throw new InvalidOperationException("capability probe child process did not start");
            Thread.Sleep(100);
            child.Close();
        }
    }
}
