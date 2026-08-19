# Supply immutable SDK and runtime image references from the release lock.
ARG SDK_IMAGE
ARG RUNTIME_IMAGE

FROM ${SDK_IMAGE} AS publish
WORKDIR /source
COPY . .
RUN dotnet restore src/RuntimeJobs/SharpLabNext.Runner/SharpLabNext.Runner.csproj --locked-mode \
    && dotnet restore src/RuntimeJobs/SharpLabNext.JitInspector/SharpLabNext.JitInspector.csproj --locked-mode \
    && dotnet publish src/RuntimeJobs/SharpLabNext.Runner/SharpLabNext.Runner.csproj \
      --configuration Release --no-restore --output /out/runner \
    && dotnet publish src/RuntimeJobs/SharpLabNext.JitInspector/SharpLabNext.JitInspector.csproj \
      --configuration Release --no-restore --output /out/jit

FROM ${RUNTIME_IMAGE} AS runtime
ARG RUNTIME_COMMIT=unknown
ARG JIT_COMMIT=unknown
LABEL org.opencontainers.image.revision=${RUNTIME_COMMIT} \
      dev.sharplabnext.jit.commit=${JIT_COMMIT} \
      dev.sharplabnext.runtime-profile.schema=1
WORKDIR /workspace
COPY --from=publish --chown=1654:1654 /out/runner/ /opt/sharplabnext/
COPY --from=publish --chown=1654:1654 /out/jit/ /opt/sharplabnext/
USER 1654:1654
