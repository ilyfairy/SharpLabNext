#define SHARPLABNEXT_JIT_PROFILER_SELF_TEST
#include "CorProfiler.cpp"

extern "C" bool minipal_guid_equals(GUID const* left, GUID const* right)
{
    return std::memcmp(left, right, sizeof(GUID)) == 0;
}

int main()
{
    return RunRichMapChunkSelfTest();
}
