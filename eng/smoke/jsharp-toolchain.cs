#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0

using System.ComponentModel;
using System.Diagnostics;

return await JSharpX64ToolchainSmoke.RunAsync(args);

internal static class JSharpX64ToolchainSmoke
{
    private const string Usage =
        "Usage: dotnet run eng/smoke/jsharp-toolchain.cs -- LOCAL_IMAGE_REFERENCE";
    private const string SuccessLine =
        "J# x64 toolchain smoke passed: stdout=sharplabnext-jsharp20-x64-ok";
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromMinutes(3);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || !IsBoundedImageReference(args[0]))
        {
            Console.Error.WriteLine(Usage);
            return 64;
        }

        var imageReference = args[0];
        var containerName = $"sharplabnext-jsharp20-smoke-{Guid.NewGuid():N}";
        var startInfo = CreateDockerStartInfo(imageReference, containerName);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Console.Error.WriteLine("Could not start Docker for the J# x64 toolchain smoke.");
            return 1;
        }
        if (process is null)
        {
            Console.Error.WriteLine("Could not start Docker for the J# x64 toolchain smoke.");
            return 1;
        }

        using (process)
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(SmokeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await RemoveContainerAsync(containerName);
                Console.Error.WriteLine("The J# x64 toolchain smoke exceeded its three-minute deadline.");
                return 1;
            }

            var output = await standardOutput;
            var error = await standardError;
            if (process.ExitCode != 0)
            {
                await RemoveContainerAsync(containerName);
                ForwardFailureOutput(output, error);
                Console.Error.WriteLine(
                    $"The J# x64 toolchain smoke container exited with code {process.ExitCode}.");
                return 1;
            }

            if (!StringComparer.Ordinal.Equals(output, SuccessLine + "\n") || error.Length != 0)
            {
                ForwardFailureOutput(output, error);
                Console.Error.WriteLine(
                    "The J# x64 toolchain smoke did not return its exact success result.");
                return 1;
            }
        }

        Console.WriteLine(SuccessLine);
        return 0;
    }

    private static ProcessStartInfo CreateDockerStartInfo(
        string imageReference,
        string containerName)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "run",
            "--rm",
            $"--name={containerName}",
            "--pull=never",
            "--platform=linux/amd64",
            "--network=none",
            "--read-only",
            "--cap-drop=ALL",
            "--security-opt=no-new-privileges=true",
            "--pids-limit=128",
            "--memory=1024m",
            "--memory-swap=1024m",
            "--cpus=1.0",
            "--ulimit=nofile=512:512",
            "--init",
            "--stop-timeout=5",
            "--hostname=jsharp-smoke",
            "--tmpfs=/tmp:rw,nosuid,nodev,noexec,size=128m,mode=1777",
            "--tmpfs=/work:rw,nosuid,nodev,noexec,size=64m,mode=1777",
            "--tmpfs=/opt/wine-jsharp20/drive_c/users/root/Temp:rw,exec,nosuid,nodev,size=256m,mode=1777",
            "--workdir=/work",
            "--env=WINEPREFIX=/opt/wine-jsharp20",
            "--env=WINEARCH=win64",
            "--env=WINEDEBUG=-all",
            "--entrypoint=/bin/bash",
            imageReference,
            "-c",
            ContainerSmokeScript
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static bool IsBoundedImageReference(string value)
    {
        if (value.Length is < 1 or > 512 ||
            value[0] == '-' ||
            value.EndsWith(':') ||
            value.EndsWith('/') ||
            value.Contains("://", StringComparison.Ordinal) ||
            value.Any(static character =>
                char.IsWhiteSpace(character) ||
                char.IsControl(character) ||
                character is not (>= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or
                    '.' or '_' or '-' or '/' or ':' or '@')))
        {
            return false;
        }

        var digestSeparator = value.IndexOf('@');
        if (digestSeparator < 0)
            return true;
        if (digestSeparator == 0 || digestSeparator != value.LastIndexOf('@'))
            return false;

        const string digestPrefix = "sha256:";
        var digest = value[(digestSeparator + 1)..];
        return digest.StartsWith(digestPrefix, StringComparison.Ordinal) &&
            digest.Length == digestPrefix.Length + 64 &&
            digest[digestPrefix.Length..].All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static async Task RemoveContainerAsync(string containerName)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("rm");
        startInfo.ArgumentList.Add("--force");
        startInfo.ArgumentList.Add("--volumes");
        startInfo.ArgumentList.Add(containerName);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static void ForwardFailureOutput(string output, string error)
    {
        if (output.Length != 0)
            Console.Error.Write(output);
        if (error.Length != 0)
            Console.Error.Write(error);
    }

    private const string ContainerSmokeScript = """
        set -euo pipefail

        fail() {
            printf 'J# x64 toolchain smoke failed: %s\n' "$1" >&2
            exit 1
        }

        read_u16() {
            od -An -v -tu2 -j "$2" -N2 "$1" | tr -d '[:space:]'
        }

        read_u32() {
            od -An -v -tu4 -j "$2" -N4 "$1" | tr -d '[:space:]'
        }

        read_hex() {
            od -An -v -tx1 -j "$2" -N "$3" "$1" | tr -d '[:space:]'
        }

        require_range() {
            local path="$1" offset="$2" size="$3" label="$4" file_size
            file_size="$(stat -c '%s' "${path}")"
            if (( offset < 0 || size < 0 || offset + size > file_size )); then
                fail "${label} is outside ${path}"
            fi
        }

        validate_amd64_pe() {
            local path="$1" label="$2" pe_offset machine magic
            require_range "${path}" 0 64 "${label} DOS header"
            test "$(read_hex "${path}" 0 2)" = '4d5a' \
                || fail "${label} DOS signature is missing"
            pe_offset="$(read_u32 "${path}" 60)"
            require_range "${path}" "${pe_offset}" 26 "${label} PE header"
            test "$(read_hex "${path}" "${pe_offset}" 4)" = '50450000' \
                || fail "${label} PE signature is missing"
            machine="$(read_u16 "${path}" "$((pe_offset + 4))")"
            magic="$(read_u16 "${path}" "$((pe_offset + 24))")"
            test "${machine}" = '34404' \
                || fail "${label} machine is ${machine}, expected AMD64"
            test "${magic}" = '523' \
                || fail "${label} optional header is not PE32+"
        }

        map_rva() {
            local path="$1" section_count="$2" section_table="$3"
            local rva="$4" size="$5" label="$6"
            local index section virtual_size virtual_address raw_size raw_offset extent delta
            for ((index = 0; index < section_count; index++)); do
                section=$((section_table + (index * 40)))
                virtual_size="$(read_u32 "${path}" "$((section + 8))")"
                virtual_address="$(read_u32 "${path}" "$((section + 12))")"
                raw_size="$(read_u32 "${path}" "$((section + 16))")"
                raw_offset="$(read_u32 "${path}" "$((section + 20))")"
                extent="${virtual_size}"
                if (( raw_size > extent )); then extent="${raw_size}"; fi
                if (( virtual_address <= rva && rva + size <= virtual_address + extent )); then
                    delta=$((rva - virtual_address))
                    (( delta + size <= raw_size )) \
                        || fail "${label} is not backed by file data"
                    require_range "${path}" "$((raw_offset + delta))" "${size}" "${label}"
                    printf '%s' "$((raw_offset + delta))"
                    return
                fi
            done
            fail "${label} does not map to a PE section"
        }

        cleanup() {
            /usr/lib/wine/wineserver -k >/dev/null 2>&1 || true
        }
        trap cleanup EXIT HUP INT TERM

        prefix=/opt/wine-jsharp20
        framework64="${prefix}/drive_c/windows/Microsoft.NET/Framework64/v2.0.50727"
        test "${WINEPREFIX:-}" = "${prefix}" || fail 'the dedicated Wine prefix is not selected'
        test "${WINEARCH:-}" = 'win64' || fail 'the Wine architecture is not win64'
        test -d "${prefix}" || fail '/opt/wine-jsharp20 is missing'
        command -v wine-stable >/dev/null 2>&1 || fail 'wine-stable is missing'
        test -x /usr/lib/wine/wineserver || fail 'the explicit wineserver is missing'
        command -v od >/dev/null 2>&1 || fail 'od is missing'
        command -v dd >/dev/null 2>&1 || fail 'dd is missing'

        for file in mscorwks.dll mscorlib.dll vjc.exe vjc.exe.config vjsc.dll; do
            test -f "${framework64}/${file}" || fail "Framework64 CLR 2.0 file ${file} is missing"
        done
        tr -d '\000' < "${framework64}/vjc.exe.config" \
            | grep -Fq 'v2.0.50727' \
            || fail 'the Framework64 compiler is not configured for CLR 2.0'
        validate_amd64_pe "${framework64}/mscorwks.dll" 'CLR 2.0 execution engine'
        validate_amd64_pe "${framework64}/vjc.exe" 'J# compiler'

        export HOME=/work/home
        export XDG_RUNTIME_DIR=/tmp/runtime
        mkdir -m 0700 "${HOME}" "${XDG_RUNTIME_DIR}"
        cat > /work/JSharpX64Smoke.jsl <<'JSHARP'
        public class JSharpX64Smoke {
            public static void main(String[] args) {
                System.Console.Write("sharplabnext-jsharp20-x64-ok");
            }
        }
        JSHARP

        if ! timeout --signal=KILL 90 \
            wine-stable "${framework64}/vjc.exe" \
            /nologo /target:exe /platform:x64 \
            '/out:Z:\work\JSharpX64Smoke.exe' \
            'Z:\work\JSharpX64Smoke.jsl' \
            > /work/compiler.stdout 2> /work/compiler.stderr; then
            cat /work/compiler.stdout >&2 || true
            cat /work/compiler.stderr >&2 || true
            fail 'Framework64 vjc.exe did not compile the fixed x64 probe'
        fi
        test -s /work/JSharpX64Smoke.exe || fail 'the J# compiler did not produce an executable'

        probe=/work/JSharpX64Smoke.exe
        validate_amd64_pe "${probe}" 'J# probe'
        pe_offset="$(read_u32 "${probe}" 60)"
        section_count="$(read_u16 "${probe}" "$((pe_offset + 6))")"
        optional_size="$(read_u16 "${probe}" "$((pe_offset + 20))")"
        optional_offset=$((pe_offset + 24))
        require_range "${probe}" "${optional_offset}" "${optional_size}" 'optional header'
        data_directory_count="$(read_u32 "${probe}" "$((optional_offset + 108))")"
        (( data_directory_count >= 15 )) || fail 'the CLR data directory is missing'

        cli_directory=$((optional_offset + 112 + (14 * 8)))
        (( cli_directory + 8 <= optional_offset + optional_size )) \
            || fail 'the CLR data directory is outside the optional header'
        cli_rva="$(read_u32 "${probe}" "${cli_directory}")"
        cli_size="$(read_u32 "${probe}" "$((cli_directory + 4))")"
        (( cli_rva != 0 && cli_size >= 72 )) \
            || fail 'the CLR header is missing or truncated'

        section_table=$((optional_offset + optional_size))
        require_range "${probe}" "${section_table}" "$((section_count * 40))" 'section table'
        cli_offset="$(map_rva \
            "${probe}" "${section_count}" "${section_table}" \
            "${cli_rva}" 72 'CLR header')"
        (( $(read_u32 "${probe}" "${cli_offset}") >= 72 )) \
            || fail 'the CLR header cb is too small'
        clr_major="$(read_u16 "${probe}" "$((cli_offset + 4))")"
        clr_minor="$(read_u16 "${probe}" "$((cli_offset + 6))")"
        test "${clr_major}.${clr_minor}" = '2.5' \
            || fail "CLR header version is ${clr_major}.${clr_minor}, expected 2.5"

        metadata_rva="$(read_u32 "${probe}" "$((cli_offset + 8))")"
        metadata_size="$(read_u32 "${probe}" "$((cli_offset + 12))")"
        flags="$(read_u32 "${probe}" "$((cli_offset + 16))")"
        entry_point="$(read_u32 "${probe}" "$((cli_offset + 20))")"
        (( (flags & 0x1) != 0 )) || fail 'the J# probe is not IL-only'
        (( (flags & 0x2) == 0 && (flags & 0x20000) == 0 )) \
            || fail "the J# probe carries a 32-bit CLR flag (${flags})"
        (( (flags & 0x10) == 0 )) || fail 'the J# probe has a native entry point'
        (( entry_point != 0 )) || fail 'the J# probe has no managed entry point'
        (( metadata_rva != 0 && metadata_size >= 16 )) \
            || fail 'the CLR metadata root is missing'

        metadata_offset="$(map_rva \
            "${probe}" "${section_count}" "${section_table}" \
            "${metadata_rva}" 16 'CLR metadata root')"
        test "$(read_u32 "${probe}" "${metadata_offset}")" = "$((0x424a5342))" \
            || fail 'the CLR metadata signature is not BSJB'
        version_size="$(read_u32 "${probe}" "$((metadata_offset + 12))")"
        (( version_size > 0 && version_size <= 256 && 16 + version_size <= metadata_size )) \
            || fail 'the CLR metadata version string is invalid'
        version_offset="$(map_rva \
            "${probe}" "${section_count}" "${section_table}" \
            "$((metadata_rva + 16))" "${version_size}" 'CLR metadata version')"
        metadata_version="$(dd \
            if="${probe}" bs=1 skip="${version_offset}" count="${version_size}" status=none \
            | tr -d '\000')"
        test "${metadata_version}" = 'v2.0.50727' \
            || fail "CLR metadata version is ${metadata_version}, expected v2.0.50727"

        if ! timeout --signal=KILL 60 \
            wine-stable /work/JSharpX64Smoke.exe \
            > /work/runtime.stdout 2> /work/runtime.stderr; then
            cat /work/runtime.stdout >&2 || true
            cat /work/runtime.stderr >&2 || true
            fail 'the x64 CLR 2.0 J# probe did not run successfully'
        fi
        if test -s /work/runtime.stderr; then
            cat /work/runtime.stderr >&2
            fail 'the x64 CLR 2.0 J# probe wrote unexpected stderr'
        fi
        printf '%s' 'sharplabnext-jsharp20-x64-ok' > /work/runtime.expected
        if ! cmp -s /work/runtime.expected /work/runtime.stdout; then
            printf 'J# x64 runtime stdout mismatch: ' >&2
            od -An -v -tx1 /work/runtime.stdout >&2
            exit 1
        fi

        printf 'J# x64 toolchain smoke passed: stdout=sharplabnext-jsharp20-x64-ok\n'
        """;
}
