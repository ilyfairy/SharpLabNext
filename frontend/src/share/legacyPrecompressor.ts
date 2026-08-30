/*
 * SharpLab v2 compatibility data and algorithm. The dictionary order is part
 * of the URL protocol.
 *
 * Copyright (c) 2016-2017, Andrey Shchekin
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 * 1. Redistributions of source code must retain the above copyright notice,
 *    this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright notice,
 *    this list of conditions and the following disclaimer in the documentation
 *    and/or other materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */

const csharpRunHelp = ['/*', '  SharpLab tools in Run mode:', '    • value.Inspect()', '    • Inspect.Heap(object)', '    • Inspect.Stack(value)', '    • Inspect.MemoryGraph(value1, value2, …)', '*/'].join('\r\n')

export const legacyDictionaries = {
  csharp: [
    'using',
    'System',
    'class',
    'public',
    'void',
    'Func',
    'Task',
    'return',
    'async',
    'await',
    'string',
    'yield',
    'Action',
    'IEnumerable',
    'System.Collections.Generic',
    'System.Threading.Tasks',
    'static',
    'Program',
    'Main',
    'Console.WriteLine',
    csharpRunHelp,
    'using System;',
    'public static void Main()',
    'public static class Program',
    'Inspect.Allocations(() =>',
    'Inspect.MemoryGraph(',
  ],
  il: [
    'Main ()',
    'Program',
    'ConsoleApp',
    'cil managed',
    '.entrypoint',
    '.maxstack',
    '.assembly',
    '.class public auto ansi abstract sealed beforefieldinit',
    'extends System.Object',
    '.method public hidebysig',
    'call void [System.Console]System.Console::WriteLine(',
  ],
} as const

type DictionaryLanguage = keyof typeof legacyDictionaries

const escapeRegex = (value: string): string => value.replace(/[-\\^$*+?.()|[\]{}]/gu, '\\$&')

const dictionaryRegexes = Object.fromEntries(
  Object.entries(legacyDictionaries).map(([language, entries]) => {
    const sorted = [...entries].sort((left, right) => Math.sign(right.length - left.length))
    return [language, new RegExp(`@|(?:${sorted.map(escapeRegex).join('|')})(?=[^\\d]|$)`, 'gm')]
  }),
) as Record<DictionaryLanguage, RegExp>

const getDictionary = (languageId: string): readonly string[] | undefined => (languageId === 'csharp' || languageId === 'il' ? legacyDictionaries[languageId] : undefined)

export const legacyPrecompress = (code: string, languageId: string): string => {
  const dictionary = getDictionary(languageId)
  if (!dictionary) return code.replace('@', '@@')

  const regex = dictionaryRegexes[languageId as DictionaryLanguage]
  return code.replace(regex, (match) => {
    if (match === '@') return '@@'
    return `@${dictionary.indexOf(match)}`
  })
}

export const legacyPredecompress = (compressed: string, languageId: string): string => {
  const dictionary = getDictionary(languageId)
  if (!dictionary) return compressed.replace('@@', '@')

  return compressed.replace(/@(\d+|@)/gu, (match, token: string) => {
    if (match === '@@') return '@'
    const entry = dictionary[Number.parseInt(token, 10)]
    if (entry === undefined) throw new Error(`Unknown SharpLab dictionary token @${token}.`)
    return entry
  })
}
