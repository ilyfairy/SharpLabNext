var sourcePath = args.LastOrDefault(static argument => argument.EndsWith(".jsl", StringComparison.OrdinalIgnoreCase));
if (sourcePath is null || !File.Exists(sourcePath))
    return 64;

var source = await File.ReadAllTextAsync(sourcePath);
if (source.Contains("OUTPUT_LIMIT", StringComparison.Ordinal))
{
    await Console.Out.WriteAsync(new string('x', 128 * 1024));
    return 1;
}
if (source.Contains("DIAGNOSTIC", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync($"{sourcePath}(2,3): error VJS1234: synthetic compiler failure");
    return 1;
}
if (source.Contains("MEMORY_LIMIT", StringComparison.Ordinal))
{
    var memory = GC.AllocateUninitializedArray<byte>(256 * 1024 * 1024);
    for (var index = 0; index < memory.Length; index += 4096)
        memory[index] = 1;
    await Task.Delay(TimeSpan.FromSeconds(30));
    GC.KeepAlive(memory);
    return 1;
}
if (source.Contains("SLEEP", StringComparison.Ordinal))
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 1;
}

var output = args.FirstOrDefault(static argument => argument.StartsWith("/out:", StringComparison.OrdinalIgnoreCase));
if (output is null)
    return 64;
var outputPath = output[5..].Replace('/', Path.DirectorySeparatorChar);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
await File.WriteAllBytesAsync(outputPath, [0x4d, 0x5a, 0x00, 0x00]);
return 0;
