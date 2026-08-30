export interface AnsiSgrStyle {
  foreground: string | null
  background: string | null
  bold: boolean
  underline: boolean
  inverse: boolean
}

export interface AnsiSgrSegment {
  text: string
  style: AnsiSgrStyle
}

export interface AnsiSgrDocument {
  segments: readonly AnsiSgrSegment[]
  text: string
  copyText: string
}

export interface AnsiSgrOutputChunk {
  text: string
  channel: 'stdout' | 'stderr'
}

const escapeCode = 0x1b;
const csi = 0x9b;

const ansi16Colors = ['#0c0c0c', '#c50f1f', '#13a10e', '#c19c00', '#0037da', '#881798', '#3a96dd', '#cccccc', '#767676', '#e74856', '#16c60c', '#f9f1a5', '#3b78ff', '#b4009e', '#61d6d6', '#f2f2f2'] as const

function defaultStyle(): AnsiSgrStyle {
  return {
    foreground: null,
    background: null,
    bold: false,
    underline: false,
    inverse: false,
  }
}

function stylesEqual(left: AnsiSgrStyle, right: AnsiSgrStyle): boolean {
  return left.foreground === right.foreground && left.background === right.background && left.bold === right.bold && left.underline === right.underline && left.inverse === right.inverse
}

function sgrNumber(value: string | undefined): number | null {
  if (value === undefined) return null;
  if (value === '') return 0;
  if (!/^\d+$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

function isByte(value: number | null): value is number {
  return value !== null && value >= 0 && value <= 255;
}

function rgb(red: number, green: number, blue: number): string {
  return `#${[red, green, blue].map((component) => component.toString(16).padStart(2, '0')).join('')}`;
}

function ansi256Color(index: number): string {
  if (index < 16) return ansi16Colors[index] ?? ansi16Colors[0]
  if (index < 232) {
    const cubeIndex = index - 16
    const levels = [0, 95, 135, 175, 215, 255] as const
    return rgb(levels[Math.floor(cubeIndex / 36)] ?? 0, levels[Math.floor(cubeIndex / 6) % 6] ?? 0, levels[cubeIndex % 6] ?? 0)
  }
  const level = 8 + (index - 232) * 10
  return rgb(level, level, level)
}

class AnsiSgrParser {
  private readonly segments: AnsiSgrSegment[] = []
  private readonly textParts: string[] = []
  private readonly copyTextParts: string[] = []
  private style = defaultStyle()
  private pending = ''

  write(chunk: string): void {
    const input = this.pending + chunk
    this.pending = ''
    let cursor = 0
    let textStart = 0

    while (cursor < input.length) {
      const code = input.charCodeAt(cursor)
      if (!this.isControlStart(code)) {
        cursor += 1
        continue
      }

      this.appendText(input.slice(textStart, cursor))
      let sequenceEnd: number | null
      if (code === escapeCode) {
        if (cursor + 1 >= input.length) {
          this.pending = input.slice(cursor)
          return
        }
        const next = input.charCodeAt(cursor + 1)
        if (next === 0x5b) {
          sequenceEnd = this.consumeCsi(input, cursor, cursor + 2)
        } else if (next === 0x5d) {
          sequenceEnd = this.consumeControlString(input, cursor, true)
        } else if (next === 0x50 || next === 0x58 || next === 0x5e || next === 0x5f) {
          sequenceEnd = this.consumeControlString(input, cursor, false)
        } else {
          sequenceEnd = this.consumeEscapeSequence(input, cursor)
        }
      } else if (code === csi) {
        sequenceEnd = this.consumeCsi(input, cursor, cursor + 1)
      } else if (code === 0x9d) {
        sequenceEnd = this.consumeControlString(input, cursor, true)
      } else if (code === 0x90 || code === 0x98 || code === 0x9e || code === 0x9f) {
        sequenceEnd = this.consumeControlString(input, cursor, false)
      } else {
        this.appendControl(input[cursor] ?? '')
        sequenceEnd = cursor + 1
      }

      if (sequenceEnd === null) {
        this.pending = input.slice(cursor)
        return
      }
      cursor = sequenceEnd
      textStart = cursor
    }

    this.appendText(input.slice(textStart))
  }

  finish(): AnsiSgrDocument {
    this.appendControl(this.pending)
    this.pending = ''
    return this.snapshot()
  }

  snapshot(): AnsiSgrDocument {
    return {
      segments: this.segments,
      text: this.textParts.join(''),
      copyText: this.copyTextParts.join(''),
    }
  }

  private isControlStart(code: number): boolean {
    if (code === escapeCode || (code >= 0x7f && code <= 0x9f)) return true
    return code < 0x20 && code !== 0x09 && code !== 0x0a && code !== 0x0d
  }

  private consumeCsi(input: string, sequenceStart: number, parameterStart: number): number | null {
    let finalIndex = parameterStart
    while (finalIndex < input.length) {
      const finalCode = input.charCodeAt(finalIndex)
      if (finalCode >= 0x40 && finalCode <= 0x7e) break
      finalIndex += 1
    }
    if (finalIndex >= input.length) return null

    const sequenceEnd = finalIndex + 1
    const parameters = input.slice(parameterStart, finalIndex)
    if (input[finalIndex] === 'm' && /^[\d;:]*$/.test(parameters)) {
      this.applyParameters(parameters)
    } else {
      this.appendControl(input.slice(sequenceStart, sequenceEnd))
    }
    return sequenceEnd
  }

  private consumeControlString(input: string, sequenceStart: number, allowBell: boolean): number | null {
    let cursor = sequenceStart + 1
    while (cursor < input.length) {
      const code = input.charCodeAt(cursor)
      let end = -1
      if (allowBell && code === 0x07) {
        end = cursor + 1
      } else if (code === 0x9c) {
        end = cursor + 1
      } else if (code === escapeCode) {
        if (cursor + 1 >= input.length) return null
        if (input.charCodeAt(cursor + 1) === 0x5c) end = cursor + 2
      }
      if (end !== -1) {
        this.appendControl(input.slice(sequenceStart, end))
        return end
      }
      cursor += 1
    }
    return null
  }

  private consumeEscapeSequence(input: string, sequenceStart: number): number | null {
    let finalIndex = sequenceStart + 1
    const first = input.charCodeAt(finalIndex)
    if (first >= 0x30 && first <= 0x7e) {
      const end = finalIndex + 1
      this.appendControl(input.slice(sequenceStart, end))
      return end
    }
    if (first < 0x20 || first > 0x2f) {
      this.appendControl(input[sequenceStart] ?? '')
      return sequenceStart + 1
    }

    finalIndex += 1
    while (finalIndex < input.length) {
      const code = input.charCodeAt(finalIndex)
      if (code >= 0x30 && code <= 0x7e) {
        const end = finalIndex + 1
        this.appendControl(input.slice(sequenceStart, end))
        return end
      }
      if (code < 0x20 || code > 0x2f) {
        this.appendControl(input.slice(sequenceStart, finalIndex))
        return finalIndex
      }
      finalIndex += 1
    }
    return null
  }

  private appendControl(control: string): void {
    if (!control) return
    let visible = ''
    for (const character of control) {
      const code = character.charCodeAt(0)
      if (code === escapeCode) {
        visible += '\u241b'
      } else if (code >= 0x80 && code <= 0x9f) {
        visible += `\u241b${String.fromCharCode(code - 0x40)}`
      } else if (code < 0x20) {
        visible += String.fromCharCode(0x2400 + code)
      } else if (code === 0x7f) {
        visible += '\u2421'
      } else {
        visible += character
      }
    }
    this.appendText(visible, false)
  }

  private appendText(text: string, copy = true): void {
    if (!text) return
    this.textParts.push(text)
    if (copy) this.copyTextParts.push(text)
    const previous = this.segments[this.segments.length - 1]
    if (previous && stylesEqual(previous.style, this.style)) {
      previous.text += text
      return
    }
    this.segments.push({ text, style: { ...this.style } })
  }

  private applyParameters(parameters: string): void {
    const values = (parameters || '0').split(';')
    for (let index = 0; index < values.length; index += 1) {
      const value = values[index] ?? ''
      if (value.includes(':')) {
        this.applyColonParameter(value)
        continue
      }

      const code = sgrNumber(value)
      if (code === 38 || code === 48) {
        index = this.applyExtendedColor(values, index, code === 38)
      } else if (code !== null) {
        this.applySimpleCode(code)
      }
    }
  }

  private applyExtendedColor(values: readonly string[], index: number, foreground: boolean): number {
    const mode = sgrNumber(values[index + 1])
    if (mode === 5) {
      const color = sgrNumber(values[index + 2])
      if (isByte(color)) this.setColor(foreground, ansi256Color(color))
      return Math.min(index + 2, values.length - 1)
    }
    if (mode === 2) {
      const red = sgrNumber(values[index + 2])
      const green = sgrNumber(values[index + 3])
      const blue = sgrNumber(values[index + 4])
      if (isByte(red) && isByte(green) && isByte(blue)) {
        this.setColor(foreground, rgb(red, green, blue))
      }
      return Math.min(index + 4, values.length - 1)
    }
    return Math.min(index + 1, values.length - 1)
  }

  private applyColonParameter(parameter: string): void {
    const values = parameter.split(':')
    const code = sgrNumber(values[0])
    if (code !== 38 && code !== 48) {
      if (code !== null) this.applySimpleCode(code)
      return
    }

    const mode = sgrNumber(values[1])
    if (mode === 5) {
      const color = sgrNumber(values[2])
      if (isByte(color)) this.setColor(code === 38, ansi256Color(color))
      return
    }
    if (mode !== 2) return

    const colorStart = values.length >= 6 ? 3 : 2
    const red = sgrNumber(values[colorStart])
    const green = sgrNumber(values[colorStart + 1])
    const blue = sgrNumber(values[colorStart + 2])
    if (isByte(red) && isByte(green) && isByte(blue)) {
      this.setColor(code === 38, rgb(red, green, blue))
    }
  }

  private applySimpleCode(code: number): void {
    if (code === 0) {
      this.style = defaultStyle()
    } else if (code === 1) {
      this.style = { ...this.style, bold: true }
    } else if (code === 4) {
      this.style = { ...this.style, underline: true }
    } else if (code === 7) {
      this.style = { ...this.style, inverse: true }
    } else if (code === 22) {
      this.style = { ...this.style, bold: false }
    } else if (code === 24) {
      this.style = { ...this.style, underline: false }
    } else if (code === 27) {
      this.style = { ...this.style, inverse: false }
    } else if (code === 39) {
      this.style = { ...this.style, foreground: null }
    } else if (code === 49) {
      this.style = { ...this.style, background: null }
    } else if (code >= 30 && code <= 37) {
      this.setColor(true, ansi16Colors[code - 30] ?? null)
    } else if (code >= 40 && code <= 47) {
      this.setColor(false, ansi16Colors[code - 40] ?? null)
    } else if (code >= 90 && code <= 97) {
      this.setColor(true, ansi16Colors[code - 90 + 8] ?? null)
    } else if (code >= 100 && code <= 107) {
      this.setColor(false, ansi16Colors[code - 100 + 8] ?? null)
    }
  }

  private setColor(foreground: boolean, color: string | null): void {
    this.style = foreground ? { ...this.style, foreground: color } : { ...this.style, background: color }
  }
}

export function parseAnsiSgrChunks(chunks: readonly string[]): AnsiSgrDocument {
  const parser = new AnsiSgrParser()
  for (const chunk of chunks) parser.write(chunk)
  return parser.finish()
}

/**
 * Parses stdout and stderr as independent ANSI streams while retaining their
 * event order. This keeps split escape sequences valid even when the two
 * streams are interleaved by the runtime transport.
 */
export function parseAnsiSgrOutputChunks(chunks: readonly AnsiSgrOutputChunk[]): AnsiSgrDocument {
  const parsers = new Map<AnsiSgrOutputChunk['channel'], AnsiSgrParser>()
  const previousSegmentLengths = new Map<AnsiSgrOutputChunk['channel'], number[]>()
  const previousTextLengths = new Map<AnsiSgrOutputChunk['channel'], number>()
  const previousCopyTextLengths = new Map<AnsiSgrOutputChunk['channel'], number>()
  const segments: AnsiSgrSegment[] = []
  const textParts: string[] = []
  const copyTextParts: string[] = []

  const flush = (channel: AnsiSgrOutputChunk['channel'], finish: boolean) => {
    const parser = parsers.get(channel)
    if (!parser) return
    const document = finish ? parser.finish() : parser.snapshot()
    const segmentLengths = previousSegmentLengths.get(channel) ?? []
    for (let index = 0; index < document.segments.length; index += 1) {
      const segment = document.segments[index]
      if (!segment) continue
      const previousLength = segmentLengths[index] ?? 0
      if (segment.text.length <= previousLength) continue
      const text = segment.text.slice(previousLength)
      const style = channel === 'stderr' ? { ...segment.style, foreground: '#c50f1f' } : { ...segment.style }
      segments.push({ text, style })
      segmentLengths[index] = segment.text.length
    }
    previousSegmentLengths.set(channel, segmentLengths)

    const previousTextLength = previousTextLengths.get(channel) ?? 0
    const previousCopyTextLength = previousCopyTextLengths.get(channel) ?? 0
    textParts.push(document.text.slice(previousTextLength))
    copyTextParts.push(document.copyText.slice(previousCopyTextLength))
    previousTextLengths.set(channel, document.text.length)
    previousCopyTextLengths.set(channel, document.copyText.length)
  }

  for (const chunk of chunks) {
    let parser = parsers.get(chunk.channel)
    if (!parser) {
      parser = new AnsiSgrParser()
      parsers.set(chunk.channel, parser)
    }
    parser.write(chunk.text)
    flush(chunk.channel, false)
  }
  for (const channel of parsers.keys()) flush(channel, true)

  return {
    segments,
    text: textParts.join(''),
    copyText: copyTextParts.join(''),
  }
}
