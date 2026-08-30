import { beforeEach, describe, expect, it } from 'vitest'
import type { LanguageManifest } from '../api/types'
import { jsharpDefaultSource } from './languageDefaults'
import { resetWorkbenchStore, useWorkbenchStore } from './store'

describe('selection response protection', () => {
  beforeEach(() => resetWorkbenchStore())

  it('does not apply a response after a newer selection intent exists', () => {
    const guard = {
      selectionRevision: useWorkbenchStore.getState().selectionRevision,
      workspaceRevision: useWorkbenchStore.getState().workspaceRevision,
    }
    useWorkbenchStore.getState().setSelectionIntent({
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      outputId: 'compile-check',
      runtimeId: null,
    })

    const applied = useWorkbenchStore.getState().applyResolvedSelection(
      {
        languageId: 'csharp',
        toolchainId: 'late-toolchain',
        referenceSetId: 'net10-ref',
        outputId: 'ast',
        runtimeId: null,
      },
      guard,
    )

    expect(applied).toBe(false)
    expect(useWorkbenchStore.getState().toolchainId).toBe('roslyn-stable')
    expect(useWorkbenchStore.getState().outputId).toBe('compile-check')
  })

  it('does not apply a response after the workspace changed', () => {
    const guard = {
      selectionRevision: useWorkbenchStore.getState().selectionRevision,
      workspaceRevision: useWorkbenchStore.getState().workspaceRevision,
    }
    useWorkbenchStore.getState().setSource('Console.WriteLine("new");')

    const applied = useWorkbenchStore.getState().applyResolvedSelection(
      {
        languageId: 'csharp',
        toolchainId: 'late-toolchain',
        referenceSetId: 'net10-ref',
        outputId: 'ast',
        runtimeId: null,
      },
      guard,
    )

    expect(applied).toBe(false)
    expect(useWorkbenchStore.getState().toolchainId).toBe('roslyn-main')
  })
})

describe('workspace template restoration', () => {
  beforeEach(() => resetWorkbenchStore())

  const csharp: LanguageManifest = {
    id: 'csharp',
    displayName: 'C#',
    monacoLanguageId: 'csharp',
    extensions: ['.cs'],
    defaultFileName: 'Program.cs',
    defaultSource: 'Console.WriteLine("C#");\n',
    defaultToolchainId: 'roslyn-stable',
    capabilities: ['ast'],
    legacyAliases: ['cs'],
  }

  const csharpSelection = {
    languageId: 'csharp',
    toolchainId: 'roslyn-stable',
    referenceSetId: 'net10-ref',
    outputId: 'ast',
    runtimeId: null,
  }

  const fsharp: LanguageManifest = {
    id: 'fsharp',
    displayName: 'F#',
    monacoLanguageId: 'fsharp',
    extensions: ['.fs', '.fsx'],
    defaultFileName: 'Program.fs',
    defaultSource: 'printfn "F#"\n',
    defaultToolchainId: 'fsharp-stable',
    capabilities: ['ast', 'multi-file'],
    legacyAliases: ['fs'],
  }

  const fsharpSelection = {
    ...csharpSelection,
    languageId: 'fsharp',
    toolchainId: 'fsharp-stable',
  }

  const php: LanguageManifest = {
    id: 'php',
    displayName: 'PHP',
    monacoLanguageId: 'php',
    extensions: ['.php'],
    defaultFileName: 'index.php',
    defaultSource: '<?php echo 49, PHP_EOL;\n',
    defaultToolchainId: 'peachpie-stable',
    capabilities: ['multi-file'],
    legacyAliases: ['php'],
  }

  const phpSelection = {
    ...csharpSelection,
    languageId: 'php',
    toolchainId: 'peachpie-stable',
  }

  const cppcli: LanguageManifest = {
    id: 'cppcli',
    displayName: 'C++/CLI',
    monacoLanguageId: 'cpp',
    extensions: ['.cpp'],
    defaultFileName: 'Program.cpp',
    defaultSource: 'using namespace System;\nint main() { return 0; }\n',
    defaultToolchainId: 'msvc-cppcli-netfx48',
    capabilities: ['diagnostics'],
    legacyAliases: ['cpp-cli'],
  }

  const cppcliSelection = {
    ...csharpSelection,
    languageId: 'cppcli',
    toolchainId: 'msvc-cppcli-netfx48',
    referenceSetId: 'netfx48-ref',
    outputId: 'decompiled-csharp',
  }

  const jsharp: LanguageManifest = {
    id: 'jsharp',
    displayName: 'J#',
    monacoLanguageId: 'jsharp',
    extensions: ['.jsl'],
    defaultFileName: 'Program.jsl',
    defaultSource: jsharpDefaultSource,
    defaultToolchainId: 'vjc-jsharp20',
    capabilities: ['diagnostics'],
    legacyAliases: ['j#'],
  }

  const jsharpSelection = {
    ...csharpSelection,
    languageId: 'jsharp',
    toolchainId: 'vjc-jsharp20',
    referenceSetId: 'jsharp20-ref',
    outputId: 'decompiled-csharp',
  }

  it('continues treating a restored default source as a replaceable template', () => {
    const store = useWorkbenchStore.getState()
    store.replaceWorkspace({
      files: [{ path: 'Program.fs', text: 'printfn "F#"\n' }],
      activeFile: 'Program.fs',
      sourceOrder: ['Program.fs'],
      selection: {
        ...csharpSelection,
        languageId: 'fsharp',
        toolchainId: 'fsharp-stable',
      },
      template: { fileName: 'Program.fs', source: 'printfn "F#"\n' },
    })

    useWorkbenchStore.getState().selectLanguage(csharp, csharpSelection)

    expect(useWorkbenchStore.getState()).toMatchObject({
      activeFile: 'Program.cs',
      source: csharp.defaultSource,
      sourceIsTemplate: true,
    })
  })

  it('keeps edited multi-file workspaces with their own language and restores them on return', () => {
    const store = useWorkbenchStore.getState()
    store.replaceWorkspace({
      files: [
        { path: 'Program.fs', text: 'printfn "edited"\n' },
        { path: 'Library.fs', text: 'module Library\n' },
      ],
      activeFile: 'Program.fs',
      sourceOrder: ['Library.fs', 'Program.fs'],
      selection: fsharpSelection,
      template: { fileName: 'Program.fs', source: 'printfn "F#"\n' },
    })

    useWorkbenchStore.getState().selectLanguage(csharp, csharpSelection)

    expect(useWorkbenchStore.getState()).toMatchObject({
      activeFile: 'Program.cs',
      source: csharp.defaultSource,
      sourceIsTemplate: true,
    })
    expect(useWorkbenchStore.getState().files).toEqual([{ path: 'Program.cs', text: csharp.defaultSource }])

    useWorkbenchStore.getState().selectLanguage(fsharp, fsharpSelection)

    expect(useWorkbenchStore.getState()).toMatchObject({
      activeFile: 'Program.fs',
      source: 'printfn "edited"\n',
      sourceOrder: ['Library.fs', 'Program.fs'],
      sourceIsTemplate: false,
    })
    expect(useWorkbenchStore.getState().files).toHaveLength(2)
  })

  it('uses the target language default file on the first switch instead of carrying old source', () => {
    useWorkbenchStore.getState().setSource('public static class CSharpOnly {}')

    useWorkbenchStore.getState().selectLanguage(php, phpSelection)

    expect(useWorkbenchStore.getState()).toMatchObject({
      languageId: 'php',
      activeFile: 'index.php',
      fileName: 'index.php',
      source: php.defaultSource,
      sourceOrder: ['index.php'],
      sourceIsTemplate: true,
    })
    expect(useWorkbenchStore.getState().files).toEqual([{ path: 'index.php', text: php.defaultSource }])
  })

  it('restores the current language template when the final file is closed', () => {
    const savedCsharp = 'Console.WriteLine("saved C# workspace");'
    useWorkbenchStore.getState().setSource(savedCsharp)
    useWorkbenchStore.getState().selectLanguage(php, phpSelection)
    useWorkbenchStore.getState().renameFile('index.php', 'scratch.php')
    useWorkbenchStore.getState().setSource('<?php echo "edited";')
    const before = useWorkbenchStore.getState().workspaceRevision

    expect(useWorkbenchStore.getState().removeFile('scratch.php')).toBe(true)
    expect(useWorkbenchStore.getState()).toMatchObject({
      activeFile: 'index.php',
      fileName: 'index.php',
      source: php.defaultSource,
      sourceOrder: ['index.php'],
      sourceIsTemplate: true,
      workspaceRevision: before + 1,
    })
    expect(useWorkbenchStore.getState().files).toEqual([{ path: 'index.php', text: php.defaultSource }])

    resetWorkbenchStore({ preserveLanguageWorkspaces: true })
    expect(useWorkbenchStore.getState().source).toBe(savedCsharp)
    useWorkbenchStore.getState().selectLanguage(php, phpSelection)
    expect(useWorkbenchStore.getState().source).toBe(php.defaultSource)
  })

  it('keeps ordinary multi-file close behavior and rejects unknown paths', () => {
    useWorkbenchStore.getState().selectLanguage(fsharp, fsharpSelection)
    expect(useWorkbenchStore.getState().addFile('Library.fs', 'module Library\n')).toBe(true)
    const beforeUnknown = useWorkbenchStore.getState().workspaceRevision

    expect(useWorkbenchStore.getState().removeFile('Unknown.fs')).toBe(false)
    expect(useWorkbenchStore.getState().workspaceRevision).toBe(beforeUnknown)
    expect(useWorkbenchStore.getState().removeFile('Program.fs')).toBe(true)
    expect(useWorkbenchStore.getState()).toMatchObject({
      activeFile: 'Library.fs',
      source: 'module Library\n',
      sourceOrder: ['Library.fs'],
      sourceIsTemplate: false,
      workspaceRevision: beforeUnknown + 1,
    })
    expect(useWorkbenchStore.getState().files).toEqual([{ path: 'Library.fs', text: 'module Library\n' }])
  })

  it('keeps C++/CLI source in its own persistent .cpp workspace', () => {
    useWorkbenchStore.getState().setSource('Console.WriteLine("C# only");')
    useWorkbenchStore.getState().selectLanguage(cppcli, cppcliSelection)
    useWorkbenchStore.getState().setSource('using namespace System;\nint main() { return 42; }\n')
    useWorkbenchStore.getState().selectLanguage(csharp, csharpSelection)

    expect(useWorkbenchStore.getState()).toMatchObject({
      activeFile: 'Program.cs',
      source: 'Console.WriteLine("C# only");',
    })

    useWorkbenchStore.getState().selectLanguage(cppcli, cppcliSelection)
    expect(useWorkbenchStore.getState()).toMatchObject({
      languageId: 'cppcli',
      activeFile: 'Program.cpp',
      source: 'using namespace System;\nint main() { return 42; }\n',
    })
  })

  it('keeps the J# .jsl workspace separate and restores it after a store refresh', () => {
    useWorkbenchStore.getState().setSource('Console.WriteLine("C# only");')
    useWorkbenchStore.getState().selectLanguage(jsharp, jsharpSelection)

    expect(useWorkbenchStore.getState()).toMatchObject({
      languageId: 'jsharp',
      activeFile: 'Program.jsl',
      source: jsharpDefaultSource,
      sourceIsTemplate: true,
    })

    const edited = jsharpDefaultSource.replace('Hello from J#', 'Saved J# workspace')
    useWorkbenchStore.getState().setSource(edited)
    useWorkbenchStore.getState().selectLanguage(csharp, csharpSelection)
    resetWorkbenchStore({ preserveLanguageWorkspaces: true })
    useWorkbenchStore.getState().selectLanguage(jsharp, jsharpSelection)

    expect(useWorkbenchStore.getState()).toMatchObject({
      languageId: 'jsharp',
      activeFile: 'Program.jsl',
      fileName: 'Program.jsl',
      source: edited,
      sourceOrder: ['Program.jsl'],
      sourceIsTemplate: false,
    })
    expect(useWorkbenchStore.getState().files).toEqual([{ path: 'Program.jsl', text: edited }])
  })

  it('persists each language workspace across a store refresh without adding it to selection state', () => {
    useWorkbenchStore.getState().setSource('Console.WriteLine("cached C#");')
    useWorkbenchStore.getState().selectLanguage(php, phpSelection)
    useWorkbenchStore.getState().setSource('<?php echo "cached PHP";')

    resetWorkbenchStore({ preserveLanguageWorkspaces: true })

    expect(useWorkbenchStore.getState()).toMatchObject({
      languageId: 'csharp',
      toolchainId: 'roslyn-main',
      referenceSetId: 'net11-preview-ref',
      outputId: 'decompiled-csharp',
      runtimeId: null,
      activeFile: 'Program.cs',
      source: 'Console.WriteLine("cached C#");',
    })

    useWorkbenchStore.getState().selectLanguage(php, phpSelection)
    expect(useWorkbenchStore.getState()).toMatchObject({
      languageId: 'php',
      activeFile: 'index.php',
      source: '<?php echo "cached PHP";',
    })
  })
})

describe('workspace source order', () => {
  beforeEach(() => resetWorkbenchStore())

  const replaceWithFSharpWorkspace = () => {
    useWorkbenchStore.getState().replaceWorkspace({
      files: [
        { path: 'Library.fs', text: 'module Library\n' },
        { path: 'Middle.fs', text: 'module Middle\n' },
        { path: 'Program.fs', text: 'printfn "F#"\n' },
      ],
      activeFile: 'Middle.fs',
      sourceOrder: ['Library.fs', 'Middle.fs', 'Program.fs'],
    })
  }

  it('moves a file by one position without changing the active file or contents', () => {
    replaceWithFSharpWorkspace()
    const before = useWorkbenchStore.getState()
    const files = before.files.map((file) => ({ ...file }))

    expect(before.moveFileInSourceOrder('Middle.fs', 'earlier')).toBe(true)
    expect(useWorkbenchStore.getState()).toMatchObject({
      sourceOrder: ['Middle.fs', 'Library.fs', 'Program.fs'],
      activeFile: 'Middle.fs',
      fileName: 'Middle.fs',
      source: 'module Middle\n',
      workspaceRevision: before.workspaceRevision + 1,
    })
    expect(useWorkbenchStore.getState().files).toEqual(files)

    expect(useWorkbenchStore.getState().moveFileInSourceOrder('Middle.fs', 'later')).toBe(true)
    expect(useWorkbenchStore.getState().sourceOrder).toEqual(['Library.fs', 'Middle.fs', 'Program.fs'])
    expect(useWorkbenchStore.getState().workspaceRevision).toBe(before.workspaceRevision + 2)
  })

  it('rejects unknown files and moves beyond either boundary', () => {
    replaceWithFSharpWorkspace()
    const before = useWorkbenchStore.getState()

    expect(before.moveFileInSourceOrder('Library.fs', 'earlier')).toBe(false)
    expect(before.moveFileInSourceOrder('Program.fs', 'later')).toBe(false)
    expect(before.moveFileInSourceOrder('Unknown.fs', 'later')).toBe(false)
    expect(useWorkbenchStore.getState()).toMatchObject({
      sourceOrder: ['Library.fs', 'Middle.fs', 'Program.fs'],
      workspaceRevision: before.workspaceRevision,
    })
  })
})
