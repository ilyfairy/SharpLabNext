import type { ShareWorkspaceState } from './types'

export const goldenState: ShareWorkspaceState = {
  languageId: 'csharp',
  toolchainId: 'roslyn-stable',
  referenceSetId: 'net10-ref',
  outputId: 'jit-asm',
  runtimeId: 'dotnet-10-linux-x64',
  buildMode: 'release',
  releaseVersion: '20260711.1',
  activeFile: 'Program.cs',
  sourceOrder: ['Program.cs'],
  files: [{ path: 'Program.cs', text: 'using System;\nConsole.WriteLine(42);' }],
}
