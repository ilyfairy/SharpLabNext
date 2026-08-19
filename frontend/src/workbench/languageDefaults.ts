import type { LanguageManifest } from '../api/types'

export const jsharpDefaultFileName = 'Program.jsl'
export const jsharpDisplayName = 'J#'

export const jsharpDefaultSource = [
  'public class Program {',
  '    public static void main(String[] args) {',
  '        System.Console.WriteLine("Hello from J#");',
  '    }',
  '}',
  '',
].join('\n')

export function languageForWorkbench(language: LanguageManifest): LanguageManifest {
  if (language.id !== 'jsharp') return language

  return {
    ...language,
    displayName: jsharpDisplayName,
    monacoLanguageId: 'jsharp',
    extensions: ['.jsl'],
    defaultFileName: jsharpDefaultFileName,
    defaultSource: jsharpDefaultSource,
  }
}
