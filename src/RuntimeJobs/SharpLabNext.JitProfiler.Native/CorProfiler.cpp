// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include "CorProfiler.h"

namespace
{
    constexpr ULONG32 MaximumNativeCodeVersions = 8;
    constexpr ULONG32 MaximumMapEntries = 20'000;
    constexpr ULONG32 MaximumTotalRecords = 1'000;
    constexpr ULONG32 MaximumTotalMapEntries = 200'000;
    constexpr size_t MaximumFileBytes = 8 * 1024 * 1024;
    std::mutex mapFileMutex;
    ULONG32 totalRecords = 0;
    ULONG32 totalMapEntries = 0;
    size_t totalFileBytes = 0;
    constexpr DWORD RichDebugEventId = 189;
    constexpr UINT32 RichDebugFinalChunkFlag = 0x80000000u;
    constexpr UINT32 RichDebugChunkSize = 40'000;
    constexpr UINT32 MaximumRichInlineNodes = 4'096;
    constexpr UINT32 MaximumRichMappings = 20'000;
    constexpr ULONG32 MaximumRichRecords = 1'000;
    constexpr ULONG32 MaximumTotalRichMappings = 200'000;
    constexpr size_t MaximumRichBytes = 8 * 1024 * 1024;
    constexpr size_t MaximumRichMethodBytes =
        sizeof(UINT32) * 2 +
        static_cast<size_t>(MaximumRichInlineNodes) * (sizeof(UINT_PTR) + sizeof(UINT32) * 3) +
        static_cast<size_t>(MaximumRichMappings) * (sizeof(UINT32) * 3 + sizeof(UINT8));

#ifdef SHARPLABNEXT_JIT_PROFILER_SELF_TEST
    std::atomic<int>* jitCallbackEntriesForSelfTest = nullptr;
    std::atomic<bool>* releaseJitCallbackForSelfTest = nullptr;
    std::atomic<bool>* profilerShutdownStartedForSelfTest = nullptr;
#endif

    struct RichRecordKey
    {
        UINT16 ClrInstanceId;
        UINT64 MethodId;
        UINT64 NativeVersionId;
        UINT64 IlVersionId;

        bool operator==(const RichRecordKey& other) const
        {
            return ClrInstanceId == other.ClrInstanceId &&
                MethodId == other.MethodId &&
                NativeVersionId == other.NativeVersionId &&
                IlVersionId == other.IlVersionId;
        }
    };

    struct RichRecordKeyHash
    {
        size_t operator()(const RichRecordKey& key) const
        {
            auto value = static_cast<size_t>(key.ClrInstanceId);
            value ^= static_cast<size_t>(key.MethodId) + 0x9e3779b9u + (value << 6) + (value >> 2);
            value ^= static_cast<size_t>(key.NativeVersionId) + 0x9e3779b9u + (value << 6) + (value >> 2);
            value ^= static_cast<size_t>(key.IlVersionId) + 0x9e3779b9u + (value << 6) + (value >> 2);
            return value;
        }
    };

    struct PendingRichRecord
    {
        UINT32 NextChunkIndex;
        std::vector<BYTE> Data;
    };

    std::mutex richMapFileMutex;
    ULONG32 totalRichRecords = 0;
    ULONG32 totalRichMappings = 0;
    size_t totalRichPayloadBytes = 0;
    size_t totalRichFileBytes = 0;
    std::unordered_map<RichRecordKey, PendingRichRecord, RichRecordKeyHash> pendingRichRecords;
    std::unordered_set<RichRecordKey, RichRecordKeyHash> seenRichRecords;

    bool HasExpectedModuleName(const std::string& path, const char* expectedModule)
    {
        const auto expectedLength = std::strlen(expectedModule);
        if (expectedLength == 0 || path.size() < expectedLength)
            return false;
        const auto start = path.size() - expectedLength;
        return path.compare(start, expectedLength, expectedModule) == 0 &&
            (start == 0 || path[start - 1] == '/' || path[start - 1] == '\\');
    }

    void InitializeMapFile()
    {
        const char* outputPath = std::getenv("SHARPLABNEXT_JIT_MAP_PATH");
        if (outputPath == nullptr)
            return;
        std::lock_guard<std::mutex> guard(mapFileMutex);
        if (FILE* file = std::fopen(outputPath, "w"))
        {
            std::fputs("SLJM1\n", file);
            std::fclose(file);
            totalFileBytes = 6;
        }
    }

    bool RichMapRequested()
    {
        const char* outputPath = std::getenv("SHARPLABNEXT_JIT_RICH_MAP_PATH");
        return outputPath != nullptr && outputPath[0] != '\0';
    }

    void InitializeRichMapFile()
    {
        const char* outputPath = std::getenv("SHARPLABNEXT_JIT_RICH_MAP_PATH");
        if (outputPath == nullptr || outputPath[0] == '\0')
            return;
        std::lock_guard<std::mutex> guard(richMapFileMutex);
        if (FILE* file = std::fopen(outputPath, "w"))
        {
            std::fputs("SLJR1\n", file);
            std::fclose(file);
            totalRichRecords = 0;
            totalRichMappings = 0;
            totalRichPayloadBytes = 0;
            totalRichFileBytes = 6;
            pendingRichRecords.clear();
            seenRichRecords.clear();
        }
    }

    bool IsExpectedMethod(ICorProfilerInfo9* profilerInfo, FunctionID functionId)
    {
        if (profilerInfo == nullptr || functionId == 0)
            return false;

        ClassID classId = 0;
        ModuleID moduleId = 0;
        mdToken token = 0;
        ULONG32 typeArgumentCount = 0;
        if (FAILED(profilerInfo->GetFunctionInfo2(
                functionId,
                0,
                &classId,
                &moduleId,
                &token,
                0,
                &typeArgumentCount,
                nullptr)) ||
            (token & 0xff000000) != 0x06000000)
        {
            return false;
        }

        ULONG modulePathLength = 0;
        AssemblyID assemblyId = 0;
        DWORD moduleFlags = 0;
        if (FAILED(profilerInfo->GetModuleInfo2(
                moduleId,
                nullptr,
                0,
                &modulePathLength,
                nullptr,
                &assemblyId,
                &moduleFlags)) ||
            modulePathLength == 0 ||
            modulePathLength > 32'768)
        {
            return false;
        }

        std::vector<WCHAR> modulePath(modulePathLength);
        if (FAILED(profilerInfo->GetModuleInfo2(
                moduleId,
                nullptr,
                modulePathLength,
                &modulePathLength,
                modulePath.data(),
                &assemblyId,
                &moduleFlags)))
        {
            return false;
        }

        const char* expectedModule = std::getenv("SHARPLABNEXT_JIT_MAP_MODULE");
        if (expectedModule == nullptr)
            return false;
        std::string narrowModulePath;
        narrowModulePath.reserve(modulePathLength);
        for (ULONG index = 0; index + 1 < modulePathLength; index++)
        {
            if (modulePath[index] > 0x7f)
                return false;
            narrowModulePath.push_back(static_cast<char>(modulePath[index]));
        }
        return HasExpectedModuleName(narrowModulePath, expectedModule);
    }

    bool IsRichDebugProvider(ICorProfilerInfo12* profilerInfo, EVENTPIPE_PROVIDER provider)
    {
        WCHAR providerName[64];
        ULONG providerNameLength = 0;
        if (profilerInfo == nullptr ||
            FAILED(profilerInfo->EventPipeGetProviderInfo(
                provider,
                static_cast<ULONG>(sizeof(providerName) / sizeof(providerName[0])),
                &providerNameLength,
                providerName)))
        {
            return false;
        }

        constexpr WCHAR expected[] = u"Microsoft-Windows-DotNETRuntimePrivate";
        constexpr size_t expectedLength = sizeof(expected) / sizeof(expected[0]);
        if (providerNameLength != expectedLength)
            return false;
        for (size_t index = 0; index < expectedLength; index++)
        {
            if (providerName[index] != expected[index])
                return false;
        }
        return true;
    }

    template<typename T>
    bool TryRead(LPCBYTE data, size_t size, size_t& offset, T& value)
    {
        if (offset > size || sizeof(T) > size - offset)
            return false;
        std::memcpy(&value, data + offset, sizeof(T));
        offset += sizeof(T);
        return true;
    }

    bool WriteRichDebugRecord(const RichRecordKey& key, const std::vector<BYTE>& richData)
    {
        size_t richOffset = 0;
        UINT32 inlineNodeCount = 0;
        UINT32 mappingCount = 0;
        if (!TryRead(richData.data(), richData.size(), richOffset, inlineNodeCount) ||
            !TryRead(richData.data(), richData.size(), richOffset, mappingCount) ||
            inlineNodeCount == 0 ||
            inlineNodeCount > MaximumRichInlineNodes ||
            mappingCount == 0 ||
            mappingCount > MaximumRichMappings ||
            mappingCount > MaximumTotalRichMappings - totalRichMappings)
        {
            return false;
        }

        constexpr size_t inlineNodeSize = sizeof(UINT_PTR) + sizeof(UINT32) * 3;
        constexpr size_t mappingSize = sizeof(UINT32) * 3 + sizeof(UINT8);
        const size_t expectedSize =
            sizeof(UINT32) * 2 +
            static_cast<size_t>(inlineNodeCount) * inlineNodeSize +
            static_cast<size_t>(mappingCount) * mappingSize;
        if (expectedSize != richData.size())
            return false;
        richOffset += static_cast<size_t>(inlineNodeCount) * inlineNodeSize;

        char field[160];
        const auto headerLength = std::snprintf(
            field,
            sizeof(field),
            "method=%llx clr=%u nativeversion=%llx ilversion=%llx inline=%u count=%u",
            static_cast<unsigned long long>(key.MethodId),
            static_cast<unsigned>(key.ClrInstanceId),
            static_cast<unsigned long long>(key.NativeVersionId),
            static_cast<unsigned long long>(key.IlVersionId),
            inlineNodeCount,
            mappingCount);
        if (headerLength <= 0 || static_cast<size_t>(headerLength) >= sizeof(field))
            return false;
        std::string line(field, static_cast<size_t>(headerLength));

        UINT32 previousNativeOffset = 0;
        for (UINT32 index = 0; index < mappingCount; index++)
        {
            UINT32 ilOffset = 0;
            UINT32 inlinee = 0;
            UINT32 nativeOffset = 0;
            UINT8 source = 0;
            if (!TryRead(richData.data(), richData.size(), richOffset, ilOffset) ||
                !TryRead(richData.data(), richData.size(), richOffset, inlinee) ||
                !TryRead(richData.data(), richData.size(), richOffset, nativeOffset) ||
                !TryRead(richData.data(), richData.size(), richOffset, source) ||
                inlinee >= inlineNodeCount ||
                source > 0x1f ||
                (index > 0 && nativeOffset < previousNativeOffset))
            {
                return false;
            }
            previousNativeOffset = nativeOffset;

            const auto fieldLength = std::snprintf(
                field,
                sizeof(field),
                " %u:%u:%u:%u",
                nativeOffset,
                ilOffset,
                inlinee,
                static_cast<unsigned>(source));
            if (fieldLength <= 0 || static_cast<size_t>(fieldLength) >= sizeof(field))
                return false;
            line.append(field, static_cast<size_t>(fieldLength));
        }
        if (richOffset != richData.size())
            return false;
        line.push_back('\n');

        const char* outputPath = std::getenv("SHARPLABNEXT_JIT_RICH_MAP_PATH");
        if (outputPath == nullptr || outputPath[0] == '\0')
            return false;
        if (totalRichFileBytes > MaximumRichBytes ||
            line.size() > MaximumRichBytes - totalRichFileBytes)
        {
            return false;
        }
        FILE* file = std::fopen(outputPath, "a");
        if (file == nullptr)
            return false;
        const auto written = std::fwrite(line.data(), 1, line.size(), file);
        std::fclose(file);
        if (written != line.size())
            return false;
        totalRichMappings += mappingCount;
        totalRichFileBytes += line.size();
        return true;
    }

    void ProcessRichDebugChunk(
        const RichRecordKey& key,
        UINT32 chunkIndexWithFlags,
        UINT32 dataSize,
        LPCBYTE data)
    {
        if (data == nullptr || dataSize == 0 || dataSize > RichDebugChunkSize)
            return;

        const bool finalChunk = (chunkIndexWithFlags & RichDebugFinalChunkFlag) != 0;
        const UINT32 chunkIndex = chunkIndexWithFlags & ~RichDebugFinalChunkFlag;
        if ((!finalChunk && dataSize != RichDebugChunkSize) ||
            chunkIndex > MaximumRichMethodBytes / RichDebugChunkSize)
        {
            return;
        }

        std::lock_guard<std::mutex> guard(richMapFileMutex);
        auto pending = pendingRichRecords.find(key);
        if (pending == pendingRichRecords.end())
        {
            if (chunkIndex != 0 ||
                seenRichRecords.find(key) != seenRichRecords.end() ||
                totalRichRecords >= MaximumRichRecords)
            {
                return;
            }
            seenRichRecords.insert(key);
            totalRichRecords++;
            pending = pendingRichRecords.emplace(
                key,
                PendingRichRecord{ 0, {} }).first;
        }

        if (chunkIndex != pending->second.NextChunkIndex ||
            dataSize > MaximumRichMethodBytes - pending->second.Data.size() ||
            totalRichPayloadBytes > MaximumRichBytes ||
            dataSize > MaximumRichBytes - totalRichPayloadBytes)
        {
            pendingRichRecords.erase(pending);
            return;
        }

        pending->second.Data.insert(
            pending->second.Data.end(),
            data,
            data + dataSize);
        pending->second.NextChunkIndex++;
        totalRichPayloadBytes += dataSize;
        if (!finalChunk)
            return;

        auto complete = std::move(pending->second.Data);
        pendingRichRecords.erase(pending);
        WriteRichDebugRecord(key, complete);
    }

    void ProcessRichDebugEvent(
        ICorProfilerInfo9* profilerInfo,
        ULONG cbEventData,
        LPCBYTE eventData)
    {
        if (eventData == nullptr)
            return;

        size_t offset = 0;
        RichRecordKey key{};
        UINT32 chunkIndexWithFlags = 0;
        UINT32 dataSize = 0;
        if (!TryRead(eventData, cbEventData, offset, key.ClrInstanceId) ||
            !TryRead(eventData, cbEventData, offset, key.MethodId) ||
            !TryRead(eventData, cbEventData, offset, key.NativeVersionId) ||
            !TryRead(eventData, cbEventData, offset, key.IlVersionId) ||
            !TryRead(eventData, cbEventData, offset, chunkIndexWithFlags) ||
            !TryRead(eventData, cbEventData, offset, dataSize) ||
            dataSize != cbEventData - offset ||
            key.MethodId == 0 ||
            !IsExpectedMethod(profilerInfo, static_cast<FunctionID>(key.MethodId)))
        {
            return;
        }

        ProcessRichDebugChunk(
            key,
            chunkIndexWithFlags,
            dataSize,
            eventData + offset);
    }

#ifdef SHARPLABNEXT_JIT_PROFILER_SELF_TEST
    template<typename T>
    void AppendTestValue(std::vector<BYTE>& data, const T& value)
    {
        const auto start = data.size();
        data.resize(start + sizeof(T));
        std::memcpy(data.data() + start, &value, sizeof(T));
    }

    std::vector<BYTE> CreateTestRichPayload(UINT32 mappingCount)
    {
        std::vector<BYTE> data;
        const UINT32 inlineNodeCount = 1;
        AppendTestValue(data, inlineNodeCount);
        AppendTestValue(data, mappingCount);
        const UINT_PTR method = 1;
        const UINT32 zero = 0;
        AppendTestValue(data, method);
        AppendTestValue(data, zero);
        AppendTestValue(data, zero);
        AppendTestValue(data, zero);
        for (UINT32 index = 0; index < mappingCount; index++)
        {
            AppendTestValue(data, index);
            AppendTestValue(data, zero);
            AppendTestValue(data, index);
            const UINT8 source = 2;
            AppendTestValue(data, source);
        }
        return data;
    }

    void EmitTestRichPayload(const RichRecordKey& key, const std::vector<BYTE>& data)
    {
        size_t offset = 0;
        UINT32 chunkIndex = 0;
        while (offset < data.size())
        {
            const auto remaining = data.size() - offset;
            const bool finalChunk = remaining <= RichDebugChunkSize;
            const auto chunkSize = static_cast<UINT32>(
                finalChunk ? remaining : RichDebugChunkSize);
            ProcessRichDebugChunk(
                key,
                chunkIndex | (finalChunk ? RichDebugFinalChunkFlag : 0),
                chunkSize,
                data.data() + offset);
            offset += chunkSize;
            chunkIndex++;
        }
    }

    int RunRichMapChunkSelfTest()
    {
        if (setenv("SHARPLABNEXT_JIT_RICH_MAP_PATH", "/tmp/sharplabnext-rich-self-test.map", 1) != 0)
            return 1;

        const RichRecordKey first{ 1, 0x1000, 2, 3 };
        auto multiChunkPayload = CreateTestRichPayload(3'075);
        if (RichDebugChunkSize != 40'000 ||
            multiChunkPayload.size() <= 40'000 ||
            multiChunkPayload.size() > 80'000)
            return 2;
        InitializeRichMapFile();
        ProcessRichDebugChunk(first, 0, RichDebugChunkSize, multiChunkPayload.data());
        ProcessRichDebugChunk(
            first,
            RichDebugFinalChunkFlag | 1,
            static_cast<UINT32>(multiChunkPayload.size() - RichDebugChunkSize),
            multiChunkPayload.data() + RichDebugChunkSize);
        if (totalRichMappings != 3'075 || !pendingRichRecords.empty())
            return 3;

        InitializeRichMapFile();
        ProcessRichDebugChunk(first, 0, RichDebugChunkSize, multiChunkPayload.data());
        ProcessRichDebugChunk(first, 0, RichDebugChunkSize, multiChunkPayload.data());
        ProcessRichDebugChunk(
            first,
            RichDebugFinalChunkFlag | 1,
            static_cast<UINT32>(multiChunkPayload.size() - RichDebugChunkSize),
            multiChunkPayload.data() + RichDebugChunkSize);
        if (totalRichMappings != 0 || !pendingRichRecords.empty())
            return 4;

        InitializeRichMapFile();
        ProcessRichDebugChunk(
            first,
            RichDebugFinalChunkFlag | 1,
            static_cast<UINT32>(multiChunkPayload.size() - RichDebugChunkSize),
            multiChunkPayload.data() + RichDebugChunkSize);
        if (totalRichMappings != 0 || !pendingRichRecords.empty())
            return 5;

        InitializeRichMapFile();
        const BYTE malformed[] = { 1, 0, 0, 0, 1, 0, 0, 0 };
        ProcessRichDebugChunk(
            first,
            RichDebugFinalChunkFlag,
            static_cast<UINT32>(sizeof(malformed)),
            malformed);
        if (totalRichMappings != 0 || !pendingRichRecords.empty())
            return 6;

        InitializeRichMapFile();
        auto smallPayload = CreateTestRichPayload(1);
        ProcessRichDebugChunk(
            first,
            RichDebugFinalChunkFlag,
            static_cast<UINT32>(smallPayload.size()),
            smallPayload.data());
        auto second = first;
        second.NativeVersionId++;
        ProcessRichDebugChunk(
            second,
            RichDebugFinalChunkFlag,
            static_cast<UINT32>(smallPayload.size()),
            smallPayload.data());
        if (totalRichMappings != 2 || totalRichRecords != 2)
            return 7;

        InitializeRichMapFile();
        std::vector<BYTE> fullChunk(RichDebugChunkSize);
        for (UINT32 index = 0; index <= MaximumRichMethodBytes / RichDebugChunkSize; index++)
            ProcessRichDebugChunk(first, index, RichDebugChunkSize, fullChunk.data());
        if (totalRichMappings != 0 || !pendingRichRecords.empty())
            return 8;

        InitializeRichMapFile();
        for (UINT64 index = 0; index < MaximumRichRecords + 1; index++)
        {
            auto key = first;
            key.MethodId += index;
            EmitTestRichPayload(key, smallPayload);
        }
        if (totalRichRecords != MaximumRichRecords || totalRichMappings != MaximumRichRecords)
            return 9;

        InitializeRichMapFile();
        auto maximumMappingPayload = CreateTestRichPayload(MaximumRichMappings);
        for (UINT64 index = 0; index < 11; index++)
        {
            auto key = first;
            key.MethodId += index;
            EmitTestRichPayload(key, maximumMappingPayload);
        }
        if (totalRichMappings != MaximumTotalRichMappings)
            return 10;

        InitializeRichMapFile();
        for (UINT64 methodIndex = 0; methodIndex < 27; methodIndex++)
        {
            auto key = first;
            key.MethodId += methodIndex;
            for (UINT32 chunkIndex = 0; chunkIndex < 8; chunkIndex++)
                ProcessRichDebugChunk(key, chunkIndex, RichDebugChunkSize, fullChunk.data());
        }
        if (totalRichPayloadBytes != 8'360'000 || totalRichPayloadBytes > MaximumRichBytes)
            return 11;

        CallbackLifetimeGate callbackGate;
        if (!callbackGate.TryEnter())
            return 12;
        std::atomic<bool> shutdownStarted = false;
        std::atomic<bool> shutdownCompleted = false;
        std::thread shutdownThread([&]
        {
            callbackGate.BeginShutdown();
            shutdownStarted.store(true, std::memory_order_release);
            callbackGate.WaitForCallbacks();
            shutdownCompleted.store(true, std::memory_order_release);
        });
        while (!shutdownStarted.load(std::memory_order_acquire))
            std::this_thread::yield();
        const bool acceptedDuringShutdown = callbackGate.TryEnter();
        if (acceptedDuringShutdown)
            callbackGate.Exit();
        const bool completedBeforeDrain = shutdownCompleted.load(std::memory_order_acquire);
        callbackGate.Exit();
        shutdownThread.join();
        if (acceptedDuringShutdown)
            return 13;
        if (completedBeforeDrain || !shutdownCompleted.load(std::memory_order_acquire))
            return 14;

        std::atomic<int> jitCallbackEntries = 0;
        std::atomic<bool> releaseJitCallback = false;
        std::atomic<bool> profilerShutdownStarted = false;
        std::atomic<bool> profilerShutdownCompleted = false;
        std::atomic<bool> activeJitCallbackCompleted = false;
        std::atomic<bool> lateJitCallbackCompleted = false;
        std::atomic<HRESULT> activeJitCallbackResult = E_FAIL;
        std::atomic<HRESULT> lateJitCallbackResult = E_FAIL;
        std::atomic<HRESULT> profilerShutdownResult = E_FAIL;
        jitCallbackEntriesForSelfTest = &jitCallbackEntries;
        releaseJitCallbackForSelfTest = &releaseJitCallback;
        profilerShutdownStartedForSelfTest = &profilerShutdownStarted;

        CorProfiler profiler;
        std::thread activeJitCallbackThread([&]
        {
            activeJitCallbackResult.store(
                profiler.JITCompilationFinished(1, E_FAIL, FALSE),
                std::memory_order_release);
            activeJitCallbackCompleted.store(true, std::memory_order_release);
        });
        while (jitCallbackEntries.load(std::memory_order_acquire) == 0)
            std::this_thread::yield();

        std::thread profilerShutdownThread([&]
        {
            profilerShutdownResult.store(profiler.Shutdown(), std::memory_order_release);
            profilerShutdownCompleted.store(true, std::memory_order_release);
        });
        while (!profilerShutdownStarted.load(std::memory_order_acquire))
            std::this_thread::yield();

        std::thread lateJitCallbackThread([&]
        {
            lateJitCallbackResult.store(
                profiler.JITCompilationFinished(2, E_FAIL, FALSE),
                std::memory_order_release);
            lateJitCallbackCompleted.store(true, std::memory_order_release);
        });
        while (!lateJitCallbackCompleted.load(std::memory_order_acquire) &&
               jitCallbackEntries.load(std::memory_order_acquire) == 1)
        {
            std::this_thread::yield();
        }

        const bool lateJitCallbackEntered =
            jitCallbackEntries.load(std::memory_order_acquire) != 1;
        const bool lateJitCallbackReturnedBeforeDrain =
            lateJitCallbackCompleted.load(std::memory_order_acquire);
        const bool shutdownReturnedBeforeDrain =
            profilerShutdownCompleted.load(std::memory_order_acquire);
        releaseJitCallback.store(true, std::memory_order_release);

        activeJitCallbackThread.join();
        lateJitCallbackThread.join();
        profilerShutdownThread.join();
        jitCallbackEntriesForSelfTest = nullptr;
        releaseJitCallbackForSelfTest = nullptr;
        profilerShutdownStartedForSelfTest = nullptr;

        if (lateJitCallbackEntered || !lateJitCallbackReturnedBeforeDrain)
            return 15;
        if (shutdownReturnedBeforeDrain)
            return 16;
        if (!activeJitCallbackCompleted.load(std::memory_order_acquire) ||
            !profilerShutdownCompleted.load(std::memory_order_acquire) ||
            activeJitCallbackResult.load(std::memory_order_acquire) != S_OK ||
            lateJitCallbackResult.load(std::memory_order_acquire) != S_OK ||
            profilerShutdownResult.load(std::memory_order_acquire) != S_OK)
        {
            return 17;
        }

        std::remove("/tmp/sharplabnext-rich-self-test.map");
        return 0;
    }
#endif
}

CorProfiler::CorProfiler()
    : refCount(0),
      corProfilerInfo(nullptr),
      eventPipeProfilerInfo(nullptr),
      eventPipeSession(0)
{
}

CorProfiler::~CorProfiler()
{
    this->profilerInfoCallbacks.BeginShutdown();
    if (this->eventPipeProfilerInfo != nullptr && this->eventPipeSession != 0)
    {
        this->eventPipeProfilerInfo->EventPipeStopSession(this->eventPipeSession);
        this->eventPipeSession = 0;
    }
    this->profilerInfoCallbacks.WaitForCallbacks();
    if (this->eventPipeProfilerInfo != nullptr)
    {
        this->eventPipeProfilerInfo->Release();
        this->eventPipeProfilerInfo = nullptr;
    }
    if (this->corProfilerInfo != nullptr)
    {
        this->corProfilerInfo->Release();
        this->corProfilerInfo = nullptr;
    }
}

HRESULT STDMETHODCALLTYPE CorProfiler::Initialize(IUnknown *pICorProfilerInfoUnk)
{
    const auto queryResult = pICorProfilerInfoUnk->QueryInterface(
        __uuidof(ICorProfilerInfo9),
        reinterpret_cast<void **>(&this->corProfilerInfo));
    if (FAILED(queryResult))
    {
        return E_FAIL;
    }

    InitializeMapFile();
    InitializeRichMapFile();
    if (!RichMapRequested())
        return this->corProfilerInfo->SetEventMask(COR_PRF_MONITOR_JIT_COMPILATION);

    const auto eventPipeQueryResult = pICorProfilerInfoUnk->QueryInterface(
        __uuidof(ICorProfilerInfo12),
        reinterpret_cast<void **>(&this->eventPipeProfilerInfo));
    if (FAILED(eventPipeQueryResult))
        return this->corProfilerInfo->SetEventMask(COR_PRF_MONITOR_JIT_COMPILATION);

    const auto maskResult = this->eventPipeProfilerInfo->SetEventMask2(
        COR_PRF_MONITOR_JIT_COMPILATION,
        COR_PRF_HIGH_MONITOR_EVENT_PIPE);
    if (FAILED(maskResult))
        return this->corProfilerInfo->SetEventMask(COR_PRF_MONITOR_JIT_COMPILATION);

    COR_PRF_EVENTPIPE_PROVIDER_CONFIG providers[] =
    {
        { u"Microsoft-Windows-DotNETRuntime", 0x10, COR_PRF_EVENTPIPE_VERBOSE, nullptr },
        { u"Microsoft-Windows-DotNETRuntimePrivate", 0x40000, COR_PRF_EVENTPIPE_INFORMATIONAL, nullptr }
    };
    const auto sessionResult = this->eventPipeProfilerInfo->EventPipeStartSession(
        static_cast<UINT32>(sizeof(providers) / sizeof(providers[0])),
        providers,
        FALSE,
        &this->eventPipeSession);
    if (FAILED(sessionResult))
        this->eventPipeSession = 0;
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::Shutdown()
{
    this->profilerInfoCallbacks.BeginShutdown();
#ifdef SHARPLABNEXT_JIT_PROFILER_SELF_TEST
    if (profilerShutdownStartedForSelfTest != nullptr)
        profilerShutdownStartedForSelfTest->store(true, std::memory_order_release);
#endif
    if (this->eventPipeProfilerInfo != nullptr && this->eventPipeSession != 0)
    {
        this->eventPipeProfilerInfo->EventPipeStopSession(this->eventPipeSession);
        this->eventPipeSession = 0;
    }
    this->profilerInfoCallbacks.WaitForCallbacks();
    if (this->eventPipeProfilerInfo != nullptr)
    {
        this->eventPipeProfilerInfo->Release();
        this->eventPipeProfilerInfo = nullptr;
    }
    if (this->corProfilerInfo != nullptr)
    {
        this->corProfilerInfo->Release();
        this->corProfilerInfo = nullptr;
    }

    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::AppDomainCreationStarted(AppDomainID appDomainId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::AppDomainCreationFinished(AppDomainID appDomainId, HRESULT hrStatus)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::AppDomainShutdownStarted(AppDomainID appDomainId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::AppDomainShutdownFinished(AppDomainID appDomainId, HRESULT hrStatus)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::AssemblyLoadStarted(AssemblyID assemblyId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::AssemblyLoadFinished(AssemblyID assemblyId, HRESULT hrStatus)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::AssemblyUnloadStarted(AssemblyID assemblyId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::AssemblyUnloadFinished(AssemblyID assemblyId, HRESULT hrStatus)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ModuleLoadStarted(ModuleID moduleId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ModuleLoadFinished(ModuleID moduleId, HRESULT hrStatus)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ModuleUnloadStarted(ModuleID moduleId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ModuleUnloadFinished(ModuleID moduleId, HRESULT hrStatus)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ModuleAttachedToAssembly(ModuleID moduleId, AssemblyID AssemblyId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ClassLoadStarted(ClassID classId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ClassLoadFinished(ClassID classId, HRESULT hrStatus)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ClassUnloadStarted(ClassID classId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ClassUnloadFinished(ClassID classId, HRESULT hrStatus)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::FunctionUnloadStarted(FunctionID functionId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::JITCompilationStarted(FunctionID functionId, BOOL fIsSafeToBlock)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::JITCompilationFinished(FunctionID functionId, HRESULT hrStatus, BOOL fIsSafeToBlock)
{
    CallbackLifetimeGuard callback(this->profilerInfoCallbacks);
    if (!callback)
        return S_OK;
#ifdef SHARPLABNEXT_JIT_PROFILER_SELF_TEST
    if (jitCallbackEntriesForSelfTest != nullptr && releaseJitCallbackForSelfTest != nullptr)
    {
        jitCallbackEntriesForSelfTest->fetch_add(1, std::memory_order_acq_rel);
        while (!releaseJitCallbackForSelfTest->load(std::memory_order_acquire))
            std::this_thread::yield();
    }
#endif
    if (FAILED(hrStatus))
        return S_OK;

    {
        std::lock_guard<std::mutex> guard(mapFileMutex);
        if (totalRecords >= MaximumTotalRecords ||
            totalMapEntries >= MaximumTotalMapEntries ||
            totalFileBytes >= MaximumFileBytes)
        {
            return S_OK;
        }
    }

    ClassID classId = 0;
    ModuleID moduleId = 0;
    mdToken token = 0;
    ULONG32 typeArgumentCount = 0;
    if (FAILED(corProfilerInfo->GetFunctionInfo2(
            functionId,
            0,
            &classId,
            &moduleId,
            &token,
            0,
            &typeArgumentCount,
            nullptr)))
        return S_OK;
    if ((token & 0xff000000) != 0x06000000)
        return S_OK;

    ULONG modulePathLength = 0;
    AssemblyID assemblyId = 0;
    DWORD moduleFlags = 0;
    if (FAILED(corProfilerInfo->GetModuleInfo2(
            moduleId,
            nullptr,
            0,
            &modulePathLength,
            nullptr,
            &assemblyId,
            &moduleFlags)) || modulePathLength == 0)
        return S_OK;

    std::vector<WCHAR> modulePath(modulePathLength);
    if (FAILED(corProfilerInfo->GetModuleInfo2(
            moduleId,
            nullptr,
            modulePathLength,
            &modulePathLength,
            modulePath.data(),
            &assemblyId,
            &moduleFlags)))
        return S_OK;

    const char* expectedModule = std::getenv("SHARPLABNEXT_JIT_MAP_MODULE");
    const char* outputPath = std::getenv("SHARPLABNEXT_JIT_MAP_PATH");
    if (expectedModule == nullptr || outputPath == nullptr)
        return S_OK;

    std::string narrowModulePath;
    narrowModulePath.reserve(modulePathLength);
    for (ULONG index = 0; index + 1 < modulePathLength; index++)
        narrowModulePath.push_back(static_cast<char>(modulePath[index]));
    if (!HasExpectedModuleName(narrowModulePath, expectedModule))
        return S_OK;

    ULONG32 addressCount = 0;
    if (FAILED(corProfilerInfo->GetNativeCodeStartAddresses(
            functionId, 0, 0, &addressCount, nullptr)) ||
        addressCount == 0 ||
        addressCount > MaximumNativeCodeVersions)
        return S_OK;
    std::vector<UINT_PTR> addresses(addressCount);
    if (FAILED(corProfilerInfo->GetNativeCodeStartAddresses(
            functionId, 0, addressCount, &addressCount, addresses.data())))
        return S_OK;

    for (UINT_PTR address : addresses)
    {
        ULONG32 mapCount = 0;
        if (FAILED(corProfilerInfo->GetILToNativeMapping3(address, 0, &mapCount, nullptr)) ||
            mapCount == 0 ||
            mapCount > MaximumMapEntries)
            continue;
        std::vector<COR_DEBUG_IL_TO_NATIVE_MAP> map(mapCount);
        if (FAILED(corProfilerInfo->GetILToNativeMapping3(address, mapCount, &mapCount, map.data())))
            continue;

        char field[128];
        const auto headerLength = std::snprintf(
            field,
            sizeof(field),
            "handle=%zx token=%08x native=%zx count=%u",
            static_cast<size_t>(functionId),
            token,
            static_cast<size_t>(address),
            mapCount);
        if (headerLength <= 0 || static_cast<size_t>(headerLength) >= sizeof(field))
            continue;
        std::string line(field, static_cast<size_t>(headerLength));
        for (ULONG32 index = 0; index < mapCount; index++)
        {
            const auto fieldLength = std::snprintf(
                field,
                sizeof(field),
                " %d:%u:%u",
                static_cast<int>(map[index].ilOffset),
                map[index].nativeStartOffset,
                map[index].nativeEndOffset);
            if (fieldLength <= 0 || static_cast<size_t>(fieldLength) >= sizeof(field))
            {
                line.clear();
                break;
            }
            line.append(field, static_cast<size_t>(fieldLength));
        }
        if (line.empty())
            continue;
        line.push_back('\n');

        std::lock_guard<std::mutex> guard(mapFileMutex);
        if (totalRecords >= MaximumTotalRecords ||
            mapCount > MaximumTotalMapEntries - totalMapEntries ||
            line.size() > MaximumFileBytes - totalFileBytes)
        {
            break;
        }
        FILE* file = std::fopen(outputPath, "a");
        if (file == nullptr)
            break;
        const auto written = std::fwrite(line.data(), 1, line.size(), file);
        std::fclose(file);
        if (written != line.size())
            break;
        totalRecords++;
        totalMapEntries += mapCount;
        totalFileBytes += line.size();
    }
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::JITCachedFunctionSearchStarted(FunctionID functionId, BOOL *pbUseCachedFunction)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::JITCachedFunctionSearchFinished(FunctionID functionId, COR_PRF_JIT_CACHE result)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::JITFunctionPitched(FunctionID functionId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::JITInlining(FunctionID callerId, FunctionID calleeId, BOOL *pfShouldInline)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ThreadCreated(ThreadID threadId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ThreadDestroyed(ThreadID threadId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ThreadAssignedToOSThread(ThreadID managedThreadId, DWORD osThreadId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RemotingClientInvocationStarted()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RemotingClientSendingMessage(GUID *pCookie, BOOL fIsAsync)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RemotingClientReceivingReply(GUID *pCookie, BOOL fIsAsync)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RemotingClientInvocationFinished()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RemotingServerReceivingMessage(GUID *pCookie, BOOL fIsAsync)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RemotingServerInvocationStarted()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RemotingServerInvocationReturned()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RemotingServerSendingReply(GUID *pCookie, BOOL fIsAsync)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::UnmanagedToManagedTransition(FunctionID functionId, COR_PRF_TRANSITION_REASON reason)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ManagedToUnmanagedTransition(FunctionID functionId, COR_PRF_TRANSITION_REASON reason)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RuntimeSuspendStarted(COR_PRF_SUSPEND_REASON suspendReason)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RuntimeSuspendFinished()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RuntimeSuspendAborted()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RuntimeResumeStarted()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RuntimeResumeFinished()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RuntimeThreadSuspended(ThreadID threadId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RuntimeThreadResumed(ThreadID threadId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::MovedReferences(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[], ObjectID newObjectIDRangeStart[], ULONG cObjectIDRangeLength[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ObjectAllocated(ObjectID objectId, ClassID classId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ObjectsAllocatedByClass(ULONG cClassCount, ClassID classIds[], ULONG cObjects[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ObjectReferences(ObjectID objectId, ClassID classId, ULONG cObjectRefs, ObjectID objectRefIds[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RootReferences(ULONG cRootRefs, ObjectID rootRefIds[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionThrown(ObjectID thrownObjectId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionSearchFunctionEnter(FunctionID functionId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionSearchFunctionLeave()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionSearchFilterEnter(FunctionID functionId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionSearchFilterLeave()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionSearchCatcherFound(FunctionID functionId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionOSHandlerEnter(UINT_PTR __unused)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionOSHandlerLeave(UINT_PTR __unused)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionUnwindFunctionEnter(FunctionID functionId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionUnwindFunctionLeave()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionUnwindFinallyEnter(FunctionID functionId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionUnwindFinallyLeave()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionCatcherEnter(FunctionID functionId, ObjectID objectId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionCatcherLeave()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::COMClassicVTableCreated(ClassID wrappedClassId, REFGUID implementedIID, void *pVTable, ULONG cSlots)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::COMClassicVTableDestroyed(ClassID wrappedClassId, REFGUID implementedIID, void *pVTable)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionCLRCatcherFound()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ExceptionCLRCatcherExecute()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ThreadNameChanged(ThreadID threadId, ULONG cchName, WCHAR name[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::GarbageCollectionStarted(int cGenerations, BOOL generationCollected[], COR_PRF_GC_REASON reason)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::SurvivingReferences(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[], ULONG cObjectIDRangeLength[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::GarbageCollectionFinished()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::FinalizeableObjectQueued(DWORD finalizerFlags, ObjectID objectID)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::RootReferences2(ULONG cRootRefs, ObjectID rootRefIds[], COR_PRF_GC_ROOT_KIND rootKinds[], COR_PRF_GC_ROOT_FLAGS rootFlags[], UINT_PTR rootIds[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::HandleCreated(GCHandleID handleId, ObjectID initialObjectId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::HandleDestroyed(GCHandleID handleId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::InitializeForAttach(IUnknown *pCorProfilerInfoUnk, void *pvClientData, UINT cbClientData)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ProfilerAttachComplete()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ProfilerDetachSucceeded()
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ReJITCompilationStarted(FunctionID functionId, ReJITID rejitId, BOOL fIsSafeToBlock)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::GetReJITParameters(ModuleID moduleId, mdMethodDef methodId, ICorProfilerFunctionControl *pFunctionControl)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ReJITCompilationFinished(FunctionID functionId, ReJITID rejitId, HRESULT hrStatus, BOOL fIsSafeToBlock)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ReJITError(ModuleID moduleId, mdMethodDef methodId, FunctionID functionId, HRESULT hrStatus)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::MovedReferences2(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[], ObjectID newObjectIDRangeStart[], SIZE_T cObjectIDRangeLength[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::SurvivingReferences2(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[], SIZE_T cObjectIDRangeLength[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ConditionalWeakTableElementReferences(ULONG cRootRefs, ObjectID keyRefIds[], ObjectID valueRefIds[], GCHandleID rootIds[])
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::GetAssemblyReferences(const WCHAR *wszAssemblyPath, ICorProfilerAssemblyReferenceProvider *pAsmRefProvider)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::ModuleInMemorySymbolsUpdated(ModuleID moduleId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::DynamicMethodJITCompilationStarted(FunctionID functionId, BOOL fIsSafeToBlock, LPCBYTE ilHeader, ULONG cbILHeader)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::DynamicMethodJITCompilationFinished(FunctionID functionId, HRESULT hrStatus, BOOL fIsSafeToBlock)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::DynamicMethodUnloaded(FunctionID functionId)
{
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::EventPipeEventDelivered(
    EVENTPIPE_PROVIDER provider,
    DWORD eventId,
    DWORD eventVersion,
    ULONG cbMetadataBlob,
    LPCBYTE metadataBlob,
    ULONG cbEventData,
    LPCBYTE eventData,
    LPCGUID pActivityId,
    LPCGUID pRelatedActivityId,
    ThreadID eventThread,
    ULONG numStackFrames,
    UINT_PTR stackFrames[])
{
    CallbackLifetimeGuard callback(this->profilerInfoCallbacks);
    if (!callback)
        return S_OK;
    try
    {
        if (eventId == RichDebugEventId &&
            eventVersion == 0 &&
            IsRichDebugProvider(this->eventPipeProfilerInfo, provider))
        {
            ProcessRichDebugEvent(this->corProfilerInfo, cbEventData, eventData);
        }
    }
    catch (...)
    {
    }
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CorProfiler::EventPipeProviderCreated(EVENTPIPE_PROVIDER provider)
{
    CallbackLifetimeGuard callback(this->profilerInfoCallbacks);
    if (!callback)
        return S_OK;
    return S_OK;
}
