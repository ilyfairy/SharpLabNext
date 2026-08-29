NETFXSDK_ROOT='z:\opt\netfxsdk'

export NETFXKitsDir="$NETFXSDK_ROOT"
export NETFXSDKDir="$NETFXSDK_ROOT"

case "$ARCH" in
x86|x64)
    export INCLUDE="$INCLUDE;$NETFXSDK_ROOT\\Include\\um"
    export LIB="$LIB;$NETFXSDK_ROOT\\Lib\\um\\$ARCH"
    export LIBPATH="$LIBPATH;$NETFXSDK_ROOT\\Lib\\um\\$ARCH"
    ;;
esac
