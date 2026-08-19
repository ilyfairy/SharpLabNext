# Runtime profile template

1. Pin `SDK_IMAGE` and `RUNTIME_IMAGE` by digest as top-level build inputs.
2. Build the image for `linux/amd64` (or declare a separate arm64 profile).
3. Replace the runtime/JIT commit placeholders. Keep the repository tag in the
   development profile; bundle generation inspects the built image and writes
   the immutable SHA-256 image ID into the generated release lock and Compose
   overlay automatically.
4. Add the runtime to Catalog with approved artifact-runtime compatibility
   edges and to Runtime Supervisor configuration.
5. Run Run, JIT, timeout, memory, output-limit, no-network, non-root, and
   container-reaping contract tests.

The image is not a persistent service. Runtime Supervisor supplies the command
and creates a fresh container for every request.
