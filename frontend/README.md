# SharpLabNext Frontend

Vite, React, TypeScript and Monaco frontend for the SharpLabNext workbench.

```powershell
npm ci
npm run lint
npm run typecheck
npm test
npm run build
npm run dev
```

The workbench reads its catalog from the Gateway, debounces selection resolution, and keeps
workspace/selection intent in a local Zustand store. TanStack Query owns catalog, operation state,
and SSE event state. C# `roslyn-stable` Build, Compile Check, and AST operations are dispatched to
the isolated Roslyn worker. Successful PE/PDB builds are checksum-verified and published to the
Artifact Store before the Gateway emits an `artifact-produced` event. Run/JIT and artifact rendering
remain separate pipeline stages and are not simulated as compiler results.
