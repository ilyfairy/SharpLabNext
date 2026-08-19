import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { CatalogDocument } from '../api/types'
import { createCatalogFixture } from '../test/catalogFixture'
import {
  fallbackLanguage,
  languageById,
  normalizeSelectionIntent,
  outputOptionsFor,
  referenceSetById,
  referenceSetOptionsFor,
  runtimeById,
  runtimeOptionsFor,
  toolchainOptionsFor,
} from './catalog'

describe('catalog selector filtering', () => {
  it('keeps the repository C# defaults aligned on the latest compiler, reference set, and runtime', () => {
    const catalog = JSON.parse(
      readFileSync(resolve(process.cwd(), '..', 'profiles/catalog/catalog.json'), 'utf8'),
    ) as CatalogDocument
    const csharp = languageById(catalog, 'csharp')
    const roslynMain = catalog.toolchains.find((toolchain) => toolchain.id === 'roslyn-main')
    const mainPreset = catalog.presets.find((preset) => preset.id === 'csharp-main-net11-preview')

    expect(fallbackLanguage.defaultToolchainId).toBe('roslyn-main')
    expect(csharp?.defaultToolchainId).toBe('roslyn-main')
    expect(roslynMain?.defaultReferenceSetId).toBe('net11-preview-ref')
    expect(catalog.referenceSets.find((item) => item.id === 'net10-ref')?.displayName).toBe(
      '.NET 10',
    )
    expect(catalog.referenceSets.find((item) => item.id === 'net11-preview-ref')?.displayName).toBe(
      '.NET Main',
    )
    expect(
      catalog.referenceSets.find((item) => item.id === 'const-generics-ref')?.displayName,
    ).toBe('Const Generics')
    expect(catalog.referenceSets.find((item) => item.id === 'netfx48-ref')?.displayName).toBe(
      '.NET Framework 4.8',
    )
    expect(catalog.runtimes.find((item) => item.id === 'dotnet-10-linux-x64')?.displayName).toBe(
      '.NET 10',
    )
    expect(
      catalog.runtimes.find((item) => item.id === 'dotnet-11-preview-linux-x64')?.displayName,
    ).toBe('.NET Main')
    expect(mainPreset).toMatchObject({
      languageId: 'csharp',
      toolchainId: 'roslyn-main',
      referenceSetId: 'net11-preview-ref',
      defaultOutputId: 'decompiled-csharp',
      defaultRuntimeId: 'dotnet-11-preview-linux-x64',
    })
  })

  it('filters toolchains by language and outputs by effective capabilities', () => {
    const catalog = createCatalogFixture()

    expect(toolchainOptionsFor(catalog, 'fsharp').map((item) => item.id)).toEqual(['fsharp-stable'])
    expect(outputOptionsFor(catalog, 'csharp', 'roslyn-stable').map((item) => item.id)).toEqual([
      'compile-check',
      'ast',
      'il',
      'decompiled-csharp',
      'run',
      'explain',
    ])
    expect(
      outputOptionsFor(catalog, 'fsharp', 'fsharp-stable').map((item) => item.id),
    ).not.toContain('explain')
  })

  it('uses compatibility edges for reference sets and runtimes, including numeric enum values', () => {
    const catalog = createCatalogFixture()

    expect(referenceSetOptionsFor(catalog, 'roslyn-stable').map((item) => item.id)).toEqual([
      'net10-ref',
      'net11-ref',
    ])
    expect(runtimeOptionsFor(catalog, 'roslyn-stable', 'net11-ref').map((item) => item.id)).toEqual(
      ['dotnet-11-linux-x64'],
    )
  })

  it('does not expose hidden reference sets, runtimes, or presets to normal selection', () => {
    const catalog = createCatalogFixture()
    const hiddenReferenceSet = catalog.referenceSets.find((item) => item.id === 'net11-ref')
    const hiddenRuntime = catalog.runtimes.find((item) => item.id === 'dotnet-11-linux-x64')
    const hiddenPreset = catalog.presets[0]
    if (!hiddenReferenceSet || !hiddenRuntime || !hiddenPreset) {
      throw new Error('Fixture is missing hidden-selection test data.')
    }
    hiddenReferenceSet.visibility = 'hidden'
    hiddenRuntime.visibility = 'hidden'
    hiddenPreset.visibility = 'hidden'

    expect(referenceSetById(catalog, hiddenReferenceSet.id)).toBeUndefined()
    expect(runtimeById(catalog, hiddenRuntime.id)).toBeUndefined()
    expect(referenceSetOptionsFor(catalog, 'roslyn-stable').map((item) => item.id)).toEqual([
      'net10-ref',
    ])
    expect(runtimeOptionsFor(catalog, 'roslyn-stable', 'net11-ref').map((item) => item.id)).toEqual(
      [],
    )
  })

  it('normalizes incompatible dimensions without hiding the requested language', () => {
    const catalog = createCatalogFixture()
    const normalized = normalizeSelectionIntent(catalog, {
      languageId: 'fsharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net11-ref',
      outputId: 'run',
      runtimeId: 'dotnet-11-linux-x64',
    })

    expect(normalized).toEqual({
      languageId: 'fsharp',
      toolchainId: 'fsharp-stable',
      referenceSetId: 'net10-ref',
      outputId: 'run',
      runtimeId: 'dotnet-11-linux-x64',
    })
  })

  it('finds processor conversion paths instead of assuming the first compiler format is runnable', () => {
    const catalog = createCatalogFixture()
    catalog.languages.push({
      id: 'minilang',
      displayName: 'MiniLang',
      monacoLanguageId: 'plaintext',
      extensions: ['.mini'],
      defaultFileName: 'Program.mini',
      defaultSource: 'print "Hello"\n',
      defaultToolchainId: 'minilang-stable',
      capabilities: [],
      legacyAliases: [],
    })
    catalog.toolchains.push({
      id: 'minilang-stable',
      displayName: 'MiniLang Stable',
      workerId: 'minilang-stable',
      releaseTrack: 'stable',
      resolvedVersion: '1.0.0',
      defaultReferenceSetId: 'net10-ref',
      supportedLanguageIds: ['minilang'],
      allowedReferenceSetIds: ['net10-ref'],
      producesArtifactFormats: ['cil-text-v1'],
      capabilities: ['compile-check', 'generated-il'],
      metadataFeatureTags: [],
      legacyAliases: [],
      availability: { installed: true, health: 'healthy' },
    })
    catalog.artifactProcessors.push({
      id: 'il-assembler',
      displayName: 'IL Assembler',
      resolvedVersion: '1.0.0',
      workerId: 'il-assembler',
      acceptsArtifactFormats: ['cil-text-v1'],
      producesArtifactFormats: ['dotnet-managed-pe-v1'],
      capabilities: ['generated-il', 'assemble-il', 'managed-pe'],
      transformations: [
        {
          id: 'assemble-il',
          inputArtifactFormat: 'cil-text-v1',
          outputArtifactFormat: 'dotnet-managed-pe-v1',
        },
      ],
      acceptedMetadataFeatureTags: [],
      availability: { installed: true, health: 'healthy' },
    })
    catalog.outputs.push({
      id: 'generated-il',
      displayName: 'Generated IL',
      renderer: 'il',
      requiresRuntime: false,
      requiredCapabilities: ['generated-il'],
      acceptedArtifactFormats: ['cil-text-v1'],
    })
    catalog.compatibility.push(
      {
        id: 'minilang-net10',
        kind: 'toolchain-reference-set',
        fromId: 'minilang-stable',
        toId: 'net10-ref',
        allowed: true,
        requiredMetadataFeatureTags: [],
      },
      {
        id: 'cil-il-assembler',
        kind: 'artifact-processor',
        fromId: 'cil-text-v1',
        toId: 'il-assembler',
        allowed: true,
        requiredMetadataFeatureTags: [],
      },
    )

    expect(
      outputOptionsFor(catalog, 'minilang', 'minilang-stable', 'net10-ref').map((item) => item.id),
    ).toEqual(['compile-check', 'il', 'decompiled-csharp', 'run', 'generated-il'])
    expect(
      runtimeOptionsFor(catalog, 'minilang-stable', 'net10-ref', 'run').map((item) => item.id),
    ).toEqual(['dotnet-10-linux-x64', 'dotnet-11-linux-x64'])
  })

  it('offers a catalog-declared artifact output without a frontend output allowlist', () => {
    const catalog = createCatalogFixture()
    catalog.artifactProcessors.push({
      id: 'artifacts-test-javascript',
      displayName: 'Test JavaScript translator',
      resolvedVersion: '1.0.0',
      workerId: 'artifacts-test-javascript',
      acceptsArtifactFormats: ['dotnet-managed-pe-v1'],
      producesArtifactFormats: ['javascript-v1'],
      capabilities: ['javascript'],
      transformations: [],
      acceptedMetadataFeatureTags: [],
      availability: { installed: true, health: 'healthy' },
    })
    catalog.outputs.push({
      id: 'javascript',
      displayName: 'JavaScript',
      renderer: 'javascript',
      requiresRuntime: false,
      requiredCapabilities: ['managed-pe', 'javascript'],
      acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
      outputArtifactFormat: 'javascript-v1',
    })
    catalog.compatibility.push({
      id: 'managed-pe-test-javascript',
      kind: 'artifact-processor',
      fromId: 'dotnet-managed-pe-v1',
      toId: 'artifacts-test-javascript',
      allowed: true,
      requiredMetadataFeatureTags: [],
    })

    expect(outputOptionsFor(catalog, 'csharp', 'roslyn-stable', 'net10-ref')).toContainEqual(
      expect.objectContaining({ id: 'javascript' }),
    )
  })

  it('uses the matching preset output when a language cannot keep the current output', () => {
    const catalog = createCatalogFixture()
    catalog.languages.push({
      id: 'php',
      displayName: 'PHP',
      monacoLanguageId: 'php',
      extensions: ['.php'],
      defaultFileName: 'index.php',
      defaultSource: '<?php echo "Hello";\n',
      defaultToolchainId: 'peachpie-stable',
      capabilities: ['diagnostics', 'multi-file'],
      legacyAliases: ['php'],
    })
    catalog.toolchains.push({
      id: 'peachpie-stable',
      displayName: 'PeachPie Stable',
      workerId: 'peachpie-stable',
      releaseTrack: 'stable',
      resolvedVersion: '1.1.13',
      defaultReferenceSetId: 'net10-ref',
      supportedLanguageIds: ['php'],
      allowedReferenceSetIds: ['net10-ref'],
      producesArtifactFormats: ['dotnet-managed-pe-v1'],
      capabilities: ['diagnostics', 'compile-check', 'managed-pe', 'multi-file'],
      metadataFeatureTags: [],
      legacyAliases: ['peachpie'],
      availability: { installed: true, health: 'healthy' },
    })
    const processor = catalog.artifactProcessors[0]
    if (!processor) throw new Error('Missing fixture artifact processor.')
    processor.producesArtifactFormats.push('decompiled-csharp-v1')
    processor.capabilities.push('decompiled-csharp', 'execution-flow')
    catalog.outputs.push({
      id: 'decompiled-csharp',
      displayName: 'Decompiled C#',
      renderer: 'csharp',
      requiresRuntime: false,
      requiredCapabilities: ['managed-pe', 'decompiled-csharp'],
      acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
    })
    catalog.outputs.push({
      id: 'execution-flow',
      displayName: 'Execution Flow',
      renderer: 'flow',
      requiresRuntime: true,
      requiredCapabilities: ['managed-pe', 'portable-pdb', 'execution-flow', 'run'],
      acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
    })
    const runtime = catalog.runtimes[0]
    if (!runtime) throw new Error('Missing fixture runtime.')
    runtime.capabilities.push('execution-flow')
    catalog.compatibility.push({
      id: 'peachpie-net10',
      kind: 'toolchain-reference-set',
      fromId: 'peachpie-stable',
      toId: 'net10-ref',
      allowed: true,
      requiredMetadataFeatureTags: [],
    })
    catalog.presets.push({
      id: 'php-peachpie-net10',
      displayName: 'PHP / PeachPie / .NET 10',
      languageId: 'php',
      toolchainId: 'peachpie-stable',
      referenceSetId: 'net10-ref',
      defaultOutputId: 'decompiled-csharp',
      defaultRuntimeId: 'dotnet-10-linux-x64',
      legacyAliases: ['php'],
      availability: { installed: true, health: 'healthy' },
    })

    expect(
      normalizeSelectionIntent(catalog, {
        languageId: 'php',
        toolchainId: null,
        referenceSetId: null,
        outputId: 'ast',
        runtimeId: null,
      }),
    ).toEqual({
      languageId: 'php',
      toolchainId: 'peachpie-stable',
      referenceSetId: 'net10-ref',
      outputId: 'decompiled-csharp',
      runtimeId: null,
    })
    expect(
      outputOptionsFor(catalog, 'php', 'peachpie-stable', 'net10-ref').map((output) => output.id),
    ).not.toContain('execution-flow')
  })

  it('uses the catalog-gated J# workspace and defaults to Decompiled C#', () => {
    const catalog = createCatalogFixture()
    catalog.languages.push({
      id: 'jsharp',
      displayName: 'J#',
      monacoLanguageId: 'plaintext',
      extensions: ['.java'],
      defaultFileName: 'Main.java',
      defaultSource: 'class Main {}',
      defaultToolchainId: 'vjc-jsharp20',
      capabilities: ['diagnostics'],
      legacyAliases: ['j#'],
    })
    catalog.toolchains.push({
      id: 'vjc-jsharp20',
      displayName: 'Visual J# 2.0',
      workerId: 'vjc-jsharp20',
      releaseTrack: 'experimental',
      resolvedVersion: '2.0.50727.937',
      defaultReferenceSetId: 'jsharp20-ref',
      supportedLanguageIds: ['jsharp'],
      allowedReferenceSetIds: ['jsharp20-ref'],
      producesArtifactFormats: ['dotnet-framework-managed-pe-v1'],
      capabilities: ['diagnostics', 'compile-check', 'managed-pe'],
      metadataFeatureTags: [],
      legacyAliases: [],
      availability: { installed: true, health: 'healthy' },
    })
    catalog.referenceSets.push({
      id: 'jsharp20-ref',
      displayName: 'J# 2.0 / CLR 2.0',
      targetFramework: 'net20',
      digest: 'jsharp20',
      runtimeFamily: 'jsharp-clr-wine',
      requiredRuntimeFeatureTags: ['runtime.jsharp20-wine'],
      metadataFeatureTags: [],
      availability: { installed: true, health: 'healthy' },
    })
    const processor = catalog.artifactProcessors[0]
    if (!processor) throw new Error('Missing fixture artifact processor.')
    processor.acceptsArtifactFormats.push('dotnet-framework-managed-pe-v1')
    for (const output of catalog.outputs) {
      if (['il', 'decompiled-csharp'].includes(output.id)) {
        output.acceptedArtifactFormats.push('dotnet-framework-managed-pe-v1')
      }
    }
    catalog.compatibility.push(
      {
        id: 'jsharp20-toolchain-reference',
        kind: 'toolchain-reference-set',
        fromId: 'vjc-jsharp20',
        toId: 'jsharp20-ref',
        allowed: true,
        requiredMetadataFeatureTags: [],
      },
      {
        id: 'jsharp20-artifacts',
        kind: 'artifact-processor',
        fromId: 'dotnet-framework-managed-pe-v1',
        toId: 'artifacts-default',
        allowed: true,
        requiredMetadataFeatureTags: [],
      },
    )
    catalog.presets.push({
      id: 'jsharp20-x64',
      displayName: 'J# 2.0',
      languageId: 'jsharp',
      toolchainId: 'vjc-jsharp20',
      referenceSetId: 'jsharp20-ref',
      defaultOutputId: 'decompiled-csharp',
      legacyAliases: ['jsharp'],
      availability: { installed: true, health: 'healthy' },
    })

    expect(languageById(catalog, 'jsharp')).toMatchObject({
      id: 'jsharp',
      displayName: 'J#',
      monacoLanguageId: 'jsharp',
      extensions: ['.jsl'],
      defaultFileName: 'Program.jsl',
      defaultSource: expect.stringContaining('Hello from J#'),
    })
    expect(
      normalizeSelectionIntent(catalog, {
        languageId: 'jsharp',
        toolchainId: null,
        referenceSetId: null,
        outputId: 'ast',
        runtimeId: null,
      }),
    ).toEqual({
      languageId: 'jsharp',
      toolchainId: 'vjc-jsharp20',
      referenceSetId: 'jsharp20-ref',
      outputId: 'decompiled-csharp',
      runtimeId: null,
    })
  })

  it('offers only the truthful mixed-PE C++/CLI surface and routes Run to Wine', () => {
    const catalog = createCatalogFixture()
    catalog.languages.push({
      id: 'cppcli',
      displayName: 'C++/CLI',
      monacoLanguageId: 'cpp',
      extensions: ['.cpp'],
      defaultFileName: 'Program.cpp',
      defaultSource: 'using namespace System;\nint main() { return 0; }\n',
      defaultToolchainId: 'msvc-cppcli-netfx48',
      capabilities: ['diagnostics'],
      legacyAliases: ['cpp-cli'],
    })
    catalog.toolchains.push({
      id: 'msvc-cppcli-netfx48',
      displayName: 'MSVC C++/CLI',
      workerId: 'msvc-cppcli-netfx48',
      releaseTrack: 'experimental',
      resolvedVersion: '19.51.36248',
      defaultReferenceSetId: 'netfx48-ref',
      supportedLanguageIds: ['cppcli'],
      allowedReferenceSetIds: ['netfx48-ref'],
      producesArtifactFormats: ['dotnet-framework-mixed-pe-v1'],
      capabilities: ['diagnostics', 'compile-check', 'managed-pe', 'mixed-pe'],
      metadataFeatureTags: [],
      legacyAliases: [],
      availability: { installed: true, health: 'healthy' },
    })
    catalog.referenceSets.push({
      id: 'netfx48-ref',
      displayName: '.NET Framework 4.8',
      targetFramework: 'net48',
      digest: 'netfx48',
      runtimeFamily: 'netfx-clr-wine',
      requiredRuntimeFeatureTags: ['runtime.netfx48-wine'],
      metadataFeatureTags: [],
      availability: { installed: true, health: 'healthy' },
    })
    catalog.runtimes.push({
      id: 'wine-netfx48-linux-x64',
      displayName: '.NET Framework 4.8 / Wine',
      family: 'netfx-clr-wine',
      resolvedVersion: 'wine-9.0+netfx48',
      rid: 'linux-x64',
      architecture: 'x64',
      acceptedArtifactFormats: ['dotnet-framework-managed-pe-v1', 'dotnet-framework-mixed-pe-v1'],
      capabilities: ['run'],
      providedRuntimeFeatureTags: ['runtime.netfx48-wine'],
      providedMetadataFeatureTags: [],
      legacyAliases: [],
      availability: { installed: true, health: 'healthy' },
    })
    const processor = catalog.artifactProcessors[0]
    if (!processor) throw new Error('Missing fixture artifact processor.')
    processor.acceptsArtifactFormats.push('dotnet-framework-mixed-pe-v1')
    for (const output of catalog.outputs) {
      if (['il', 'decompiled-csharp', 'run'].includes(output.id)) {
        output.acceptedArtifactFormats.push('dotnet-framework-mixed-pe-v1')
      }
    }
    catalog.outputs.push(
      {
        id: 'il-verify',
        displayName: 'IL Verify',
        renderer: 'verification',
        requiresRuntime: false,
        requiredCapabilities: ['managed-pe', 'il-verify'],
        acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
      },
      {
        id: 'jit-asm',
        displayName: 'JIT ASM',
        renderer: 'asm',
        requiresRuntime: true,
        requiredCapabilities: ['managed-pe', 'jit-asm'],
        acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
      },
      {
        id: 'execution-flow',
        displayName: 'Execution Flow',
        renderer: 'flow',
        requiresRuntime: true,
        requiredCapabilities: ['managed-pe', 'portable-pdb', 'execution-flow', 'run'],
        acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
      },
      {
        id: 'run-il',
        displayName: 'Rewritten Run IL',
        renderer: 'il',
        requiresRuntime: false,
        requiredCapabilities: ['managed-pe', 'run-il'],
        acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
      },
    )
    catalog.compatibility.push(
      {
        id: 'cppcli-netfx48',
        kind: 'toolchain-reference-set',
        fromId: 'msvc-cppcli-netfx48',
        toId: 'netfx48-ref',
        allowed: true,
        requiredMetadataFeatureTags: [],
      },
      {
        id: 'mixed-artifacts',
        kind: 'artifact-processor',
        fromId: 'dotnet-framework-mixed-pe-v1',
        toId: 'artifacts-default',
        allowed: true,
        requiredMetadataFeatureTags: [],
      },
      {
        id: 'mixed-wine',
        kind: 'artifact-runtime',
        fromId: 'dotnet-framework-mixed-pe-v1',
        toId: 'wine-netfx48-linux-x64',
        allowed: true,
        requiredMetadataFeatureTags: [],
      },
    )

    expect(
      outputOptionsFor(catalog, 'cppcli', 'msvc-cppcli-netfx48', 'netfx48-ref').map(
        (output) => output.id,
      ),
    ).toEqual(['compile-check', 'il', 'decompiled-csharp', 'run'])
    expect(
      runtimeOptionsFor(catalog, 'msvc-cppcli-netfx48', 'netfx48-ref', 'run').map(
        (runtime) => runtime.id,
      ),
    ).toEqual(['wine-netfx48-linux-x64'])
    expect(
      catalog.runtimes.find((runtime) => runtime.id === 'wine-netfx48-linux-x64')
        ?.acceptedArtifactFormats,
    ).toEqual(['dotnet-framework-managed-pe-v1', 'dotnet-framework-mixed-pe-v1'])
    expect(
      catalog.toolchains.some((toolchain) =>
        toolchain.producesArtifactFormats.includes('dotnet-framework-managed-pe-v1'),
      ),
    ).toBe(false)
    expect(catalog.languages.some((language) => ['jsharp', 'j#'].includes(language.id))).toBe(false)
  })
})
