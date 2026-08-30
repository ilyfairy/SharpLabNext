#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

Process? docker = null;
Stream input;
if (args.Length == 0)
    input = Console.OpenStandardInput();
else
{
    var startInfo = new ProcessStartInfo("docker")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("logs");
    startInfo.ArgumentList.Add(args[0]);
    docker = Process.Start(startInfo) ?? throw new InvalidOperationException("Docker logs could not be started.");
    input = docker.StandardOutput.BaseStream;
}
var count = 0;
long previousSequence = 0;
long payloadBytes = 0;
using var reader = new StreamReader(input, Encoding.ASCII, false, 8 * 1024, leaveOpen: true);
while (await reader.ReadLineAsync() is { } line)
{
    if (line.Length > 5_592_432)
        throw new InvalidDataException("Encoded runtime frame exceeds the protocol limit.");
    var frame = Convert.FromBase64String(line);
    if (frame.Length < 18)
        throw new InvalidDataException("Encoded runtime frame is shorter than its header.");
    var header = frame.AsSpan(0, 18);
    if (!header[..4].SequenceEqual("SLNR"u8))
        throw new InvalidDataException($"Invalid magic after {count} frames: {Convert.ToHexString(header)}");
    if (header[4] != 1)
        throw new InvalidDataException($"Unsupported protocol version {header[4]}.");
    var sequence = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(6, 8));
    var length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(14, 4));
    if (sequence <= previousSequence || length < 0 || length > 4 * 1024 * 1024)
    {
        throw new InvalidDataException($"Invalid frame after {count} frames: sequence={sequence}, previous={previousSequence}, kind={header[5]}, length={length}, header={Convert.ToHexString(header)}");
    }
    if (frame.Length != 18 + length)
        throw new InvalidDataException($"Frame {sequence} length does not match its encoded line.");
    previousSequence = sequence;
    payloadBytes += length;
    count++;
}

Console.WriteLine($"Decoded {count} runtime frames containing {payloadBytes} payload bytes.");
if (docker is not null)
{
    var stderr = await docker.StandardError.ReadToEndAsync();
    await docker.WaitForExitAsync();
    if (docker.ExitCode != 0)
        throw new InvalidOperationException($"Docker logs failed with {docker.ExitCode}: {stderr}");
    docker.Dispose();
}
