import { describe, expect, it } from 'vitest'
import { composeJitAssembly, jitAssemblySourceTooltips, parseJitAssembly, preferredJitSectionId, remapJitLineRange } from './jitAssemblyModel'

const text = `; Assembly listing for method Program:<Main>$(System.String[]):int (FullOpts)
G_M000_IG01:
  ret
; Total bytes of code 1

; Assembly listing for method MyClass:Sum[int,int]():int (FullOpts)
G_M001_IG01:
  add eax, 2
  ret
; Total bytes of code 4`

describe('jitAssemblyModel', () => {
  it('splits raw JIT output into methods', () => {
    const sections = parseJitAssembly(text, [
      {
        methodId: 'main',
        displayName: 'Program.<Main>$',
        nativeCodeSize: 1,
        instructionCount: 1,
        linkedRanges: [],
      },
      {
        methodId: 'sum',
        displayName: 'MyClass.Sum',
        nativeCodeSize: 4,
        instructionCount: 2,
        linkedRanges: [],
      },
    ])
    expect(sections).toHaveLength(2)
    expect(sections[1]?.text).toBe(`MyClass:Sum[int,int]():int:
  add eax, 2
  ret`)
    expect(composeJitAssembly(sections)).not.toContain('; Assembly listing for method')
    expect(composeJitAssembly(sections)).not.toContain('; Total bytes of code')
    expect(composeJitAssembly(sections)).toContain('Program:<Main>$(System.String[]):int:')
    expect(preferredJitSectionId(sections, 'Sum')).toBe('sum')
  })

  it('reduces an unbranched method to its signature and instructions', () => {
    const raw = `; Assembly listing for method Program:<<Main>$>g__B|0_1(int,int):double (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Unix
; FullOpts code
; optimized code
; rsp based frame
; partially interruptible
; No PGO data

G_M000_IG01:

G_M000_IG02:
       add      edi, esi
       vxorps   xmm0, xmm0, xmm0
       vcvtsi2sd xmm0, xmm0, edi

G_M000_IG03:
       ret
; Total bytes of code 11`

    expect(parseJitAssembly(raw)[0]?.text).toBe(`Program:<<Main>$>g__B|0_1(int,int):double:
       add      edi, esi
       vxorps   xmm0, xmm0, xmm0
       vcvtsi2sd xmm0, xmm0, edi
       ret`)
    const section = parseJitAssembly(raw)[0]
    expect(section?.rawLineToCompactLine).toHaveLength(18)
    expect(section?.rawLineToCompactLine.filter((line) => line !== null)).toEqual([0, 1, 2, 3, 4])
    expect(section?.rawLineToCompactLine[11]).toBe(1)
    expect(section?.rawLineToCompactLine[12]).toBe(2)
    expect(section?.rawLineToCompactLine[13]).toBe(3)
    expect(section?.rawLineToCompactLine[16]).toBe(4)
    expect(section && remapJitLineRange(section, 8, 13)).toEqual({
      startLine: 1,
      endLine: 3,
    })
    expect(section && remapJitLineRange(section, 1, 7)).toBeNull()
  })

  it('keeps branch targets but removes unreferenced block labels', () => {
    const raw = `; Assembly listing for method Program:Choose(int):int (FullOpts)
G_M000_IG01:
       test     edi, edi
       je       SHORT G_M000_IG03

G_M000_IG02:
       mov      eax, 1
       jmp      G_M000_IG04

G_M000_IG03:
       xor      eax, eax

G_M000_IG04:
       ret
; Total bytes of code 12`

    expect(parseJitAssembly(raw)[0]?.text).toBe(`Program:Choose(int):int:
       test     edi, edi
       je       SHORT G_M000_IG03
       mov      eax, 1
       jmp      G_M000_IG04
G_M000_IG03:
       xor      eax, eax
G_M000_IG04:
       ret`)
    const section = parseJitAssembly(raw)[0]
    expect(section?.rawLineToCompactLine[1]).toBeNull()
    expect(section?.rawLineToCompactLine[5]).toBeNull()
    expect(section?.rawLineToCompactLine[9]).toBe(5)
    expect(section?.rawLineToCompactLine[12]).toBe(7)
    expect(section && remapJitLineRange(section, 1, 6)).toEqual({
      startLine: 1,
      endLine: 3,
    })
  })

  it('preserves meaningful full-line and instruction comments', () => {
    const raw = `; Assembly listing for method Program:Meaningful():int (FullOpts)
; Source line 12 performs the addition
G_M000_IG01:
       mov      eax, 40 ; first operand
       add      eax, 2  ; source: return 40 + 2
       ret
; 0 inlinees with PGO data; 0 inlinees without PGO data
; Total bytes of code 8`

    expect(parseJitAssembly(raw)[0]?.text).toBe(`Program:Meaningful():int:
; Source line 12 performs the addition
       mov      eax, 40 ; first operand
       add      eax, 2  ; source: return 40 + 2
       ret`)
  })

  it('hides internal inline source-map markers without shifting instruction mappings', () => {
    const raw = `; Assembly listing for method Program:Mapped():int (FullOpts)
; INLRT @ 0 [000000] in Program:Mapped():int
; INL00 @ 5 [000001] in Program:Helper():int <- INLRT
G_M000_IG01:
       mov      eax, 40
; INL01 @ 8 [000002] in Program:Other():int <- INLRT [01]
       add      eax, 2
       ret
; Total bytes of code 8`

    const section = parseJitAssembly(raw)[0]
    expect(section?.text).toBe(`Program:Mapped():int:
       mov      eax, 40
       add      eax, 2
       ret`)
    expect(section?.rawLineToCompactLine[1]).toBeNull()
    expect(section?.rawLineToCompactLine[2]).toBeNull()
    expect(section?.rawLineToCompactLine[5]).toBeNull()
    expect(section && remapJitLineRange(section, 1, 6)).toEqual({
      startLine: 1,
      endLine: 2,
    })
  })

  it('ignores helper output instead of assigning summaries by raw section position', () => {
    const noisyText = `; Assembly listing for method (dynamicClass):IL_STUB_ReversePInvoke(System.IntPtr):int (FullOpts)
  ret
; Total bytes of code 1

; Assembly listing for method (dynamicClass):IL_STUB_PInvoke(System.IntPtr):int (FullOpts)
  ret
; Total bytes of code 1

; Assembly listing for method Program:<Main>$(System.String[]):int (FullOpts)
  call JitInspectorProgram:RunAsync(System.String[])
; Total bytes of code 6

; Assembly listing for method JitInspectorProgram:RunAsync(System.String[]):int (FullOpts)
  ret
; Total bytes of code 1

; Assembly listing for method Program:Other():int (FullOpts)
  ret
; Total bytes of code 3

; Assembly listing for method Program:CurrentTarget():int (FullOpts)
  call Program:Other():int
  ret
; Total bytes of code 8

; Assembly listing for method Program:Main() (FullOpts)
  ret
; Total bytes of code 2`

    const sections = parseJitAssembly(noisyText, [
      {
        methodId: 'other',
        displayName: 'Program.Other',
        nativeCodeSize: 3,
        instructionCount: 1,
        linkedRanges: [],
      },
      {
        methodId: 'current',
        displayName: 'Program.CurrentTarget',
        nativeCodeSize: 8,
        instructionCount: 2,
        linkedRanges: [],
      },
      {
        methodId: 'main',
        displayName: 'Program.Main',
        nativeCodeSize: 2,
        instructionCount: 1,
        linkedRanges: [],
      },
    ])

    expect(sections.map((section) => section.displayName)).toEqual(['Program.Other', 'Program.CurrentTarget', 'Program.Main'])
    const current = sections.find((section) => section.id === 'current')
    expect(current?.text).toContain('Program:CurrentTarget')
    expect(current?.text).not.toContain('(dynamicClass)')
    expect(current?.text).not.toContain('Program:<Main>$')
    expect(preferredJitSectionId(sections, 'CurrentTarget')).toBe('current')
  })

  it('maps generated local-function methods to approximate source declarations', () => {
    const generatedText = `; Assembly listing for method Program:<<Main>$>g__B|0_1(int,int):double (FullOpts)
G_M000_IG01:
  vxorps xmm12, xmm12, xmm12
  ret
; Total bytes of code 8`
    const sections = parseJitAssembly(
      generatedText,
      [
        {
          methodId: 'b',
          displayName: 'Program.<<Main>$>g__B|0_1',
          nativeCodeSize: 8,
          instructionCount: 2,
          linkedRanges: [],
        },
      ],
      [
        {
          path: 'Program.cs',
          text: 'using System;\n\ndouble B(int a, int b) => a + b;',
        },
      ],
    )

    expect(preferredJitSectionId(sections, 'B')).toBe('b')
    expect(sections[0]?.source).toEqual({
      documentPath: 'Program.cs',
      lineNumber: 3,
      code: 'double B(int a, int b) => a + b;',
    })
    expect(jitAssemblySourceTooltips(sections)).toEqual([
      {
        startLine: 1,
        endLine: 3,
        heading: 'Approximate source: Program.cs:3',
        body: 'double B(int a, int b) => a + b;',
      },
    ])
    expect(composeJitAssembly(sections)).not.toContain('JitInspectorProgram')
  })
})
