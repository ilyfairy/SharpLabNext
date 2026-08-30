import { ShareUrlError } from './errors'

const lengthBases = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258] as const
const lengthExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0] as const
const distanceBases = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1_025, 1_537, 2_049, 3_073, 4_097, 6_145, 8_193, 12_289, 16_385, 24_577] as const
const distanceExtraBits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13] as const
const codeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15] as const

class BitReader {
  private readonly data: Uint8Array
  private bitOffset = 0

  constructor(data: Uint8Array) {
    this.data = data
  }

  readBits(count: number): number {
    if (count < 0 || count > 16 || this.bitOffset + count > this.data.length * 8) {
      throw new ShareUrlError('decompression-failed', 'The raw DEFLATE stream is truncated.')
    }

    let result = 0
    for (let bit = 0; bit < count; bit += 1) {
      const absolute = this.bitOffset + bit
      const byte = this.data[absolute >>> 3] ?? 0
      result |= ((byte >>> (absolute & 7)) & 1) << bit
    }
    this.bitOffset += count
    return result
  }

  readHuffmanBit(): number {
    return this.readBits(1)
  }

  alignToByte(): void {
    this.bitOffset = (this.bitOffset + 7) & ~7
  }

  get consumedBytes(): number {
    return Math.ceil(this.bitOffset / 8)
  }
}

interface HuffmanTable {
  maxBits: number
  symbols: Map<number, number>
}

const buildHuffmanTable = (lengths: readonly number[], name: string): HuffmanTable => {
  const maxBits = lengths.reduce((maximum, length) => Math.max(maximum, length), 0)
  if (maxBits === 0 || maxBits > 15 || lengths.some((length) => length < 0 || length > 15)) {
    throw new ShareUrlError('decompression-failed', `The ${name} Huffman tree is invalid.`)
  }

  const counts = new Array<number>(maxBits + 1).fill(0)
  for (const length of lengths) {
    if (length > 0) counts[length] = (counts[length] ?? 0) + 1
  }

  let remaining = 1
  for (let bits = 1; bits <= maxBits; bits += 1) {
    remaining = remaining * 2 - (counts[bits] ?? 0)
    if (remaining < 0) {
      throw new ShareUrlError('decompression-failed', `The ${name} Huffman tree is oversubscribed.`)
    }
  }

  const nextCodes = new Array<number>(maxBits + 1).fill(0)
  let code = 0
  for (let bits = 1; bits <= maxBits; bits += 1) {
    code = (code + (counts[bits - 1] ?? 0)) << 1
    nextCodes[bits] = code
  }

  const symbols = new Map<number, number>()
  for (let symbol = 0; symbol < lengths.length; symbol += 1) {
    const length = lengths[symbol] ?? 0
    if (length === 0) continue
    const symbolCode = nextCodes[length] ?? 0
    nextCodes[length] = symbolCode + 1
    symbols.set((length << 16) | symbolCode, symbol)
  }
  return { maxBits, symbols }
}

const decodeSymbol = (reader: BitReader, table: HuffmanTable): number => {
  let code = 0
  for (let length = 1; length <= table.maxBits; length += 1) {
    code = (code << 1) | reader.readHuffmanBit()
    const symbol = table.symbols.get((length << 16) | code)
    if (symbol !== undefined) return symbol
  }
  throw new ShareUrlError('decompression-failed', 'The raw DEFLATE stream has an invalid code.')
}

const fixedLiteralLengths = new Array<number>(288).fill(0).map((_, symbol) => {
  if (symbol <= 143) return 8
  if (symbol <= 255) return 9
  if (symbol <= 279) return 7
  return 8
})
const fixedDistanceLengths = new Array<number>(32).fill(5)
const fixedLiteralTable = buildHuffmanTable(fixedLiteralLengths, 'fixed literal/length')
const fixedDistanceTable = buildHuffmanTable(fixedDistanceLengths, 'fixed distance')

const readDynamicTables = (reader: BitReader): { literalTable: HuffmanTable; distanceTable: HuffmanTable | null } => {
  const literalCount = reader.readBits(5) + 257
  const distanceCount = reader.readBits(5) + 1
  const codeLengthCount = reader.readBits(4) + 4
  if (literalCount > 286) {
    throw new ShareUrlError('decompression-failed', 'The DEFLATE literal code count is invalid.')
  }
  const codeLengths = new Array<number>(19).fill(0)
  for (let index = 0; index < codeLengthCount; index += 1) {
    codeLengths[codeLengthOrder[index] ?? 0] = reader.readBits(3)
  }
  const codeLengthTable = buildHuffmanTable(codeLengths, 'code-length')

  const total = literalCount + distanceCount
  const lengths: number[] = []
  while (lengths.length < total) {
    const symbol = decodeSymbol(reader, codeLengthTable)
    if (symbol <= 15) {
      lengths.push(symbol)
      continue
    }

    let repeatedLength: number
    let repeatCount: number
    if (symbol === 16) {
      if (lengths.length === 0) {
        throw new ShareUrlError('decompression-failed', 'A DEFLATE repeat code has no prior value.')
      }
      repeatedLength = lengths[lengths.length - 1] ?? 0
      repeatCount = reader.readBits(2) + 3
    } else if (symbol === 17) {
      repeatedLength = 0
      repeatCount = reader.readBits(3) + 3
    } else if (symbol === 18) {
      repeatedLength = 0
      repeatCount = reader.readBits(7) + 11
    } else {
      throw new ShareUrlError('decompression-failed', 'The DEFLATE code-length tree is invalid.')
    }

    if (lengths.length + repeatCount > total) {
      throw new ShareUrlError('decompression-failed', 'A DEFLATE repeat exceeds its code table.')
    }
    for (let index = 0; index < repeatCount; index += 1) lengths.push(repeatedLength)
  }

  const literalLengths = lengths.slice(0, literalCount)
  if ((literalLengths[256] ?? 0) === 0) {
    throw new ShareUrlError('decompression-failed', 'The DEFLATE literal tree has no end marker.')
  }
  const distanceLengths = lengths.slice(literalCount)
  return {
    literalTable: buildHuffmanTable(literalLengths, 'literal/length'),
    distanceTable: distanceLengths.every((length) => length === 0) ? null : buildHuffmanTable(distanceLengths, 'distance'),
  }
}

const addOutput = (current: number, addition: number, expectedLength: number): number => {
  const next = current + addition
  if (next > expectedLength) {
    throw new ShareUrlError('length-mismatch', 'The raw DEFLATE stream expands beyond its declared payload length.')
  }
  return next
}

const readCompressedBlock = (reader: BitReader, literalTable: HuffmanTable, distanceTable: HuffmanTable | null, initialOutputLength: number, expectedLength: number): number => {
  let outputLength = initialOutputLength
  while (true) {
    const symbol = decodeSymbol(reader, literalTable)
    if (symbol < 256) {
      outputLength = addOutput(outputLength, 1, expectedLength)
      continue
    }
    if (symbol === 256) return outputLength
    if (symbol < 257 || symbol > 285) {
      throw new ShareUrlError('decompression-failed', 'The DEFLATE stream has an invalid length code.')
    }

    const lengthIndex = symbol - 257
    const matchLength = (lengthBases[lengthIndex] ?? 0) + reader.readBits(lengthExtraBits[lengthIndex] ?? 0)
    if (!distanceTable) {
      throw new ShareUrlError('decompression-failed', 'The DEFLATE stream uses a match without a distance tree.')
    }
    const distanceSymbol = decodeSymbol(reader, distanceTable)
    if (distanceSymbol > 29) {
      throw new ShareUrlError('decompression-failed', 'The DEFLATE stream has an invalid distance code.')
    }
    const distance = (distanceBases[distanceSymbol] ?? 0) + reader.readBits(distanceExtraBits[distanceSymbol] ?? 0)
    if (distance === 0 || distance > outputLength) {
      throw new ShareUrlError('decompression-failed', 'The DEFLATE stream references invalid history.')
    }
    outputLength = addOutput(outputLength, matchLength, expectedLength)
  }
}

export const validateDeflateRaw = (data: Uint8Array, expectedLength: number): void => {
  if (data.length === 0) {
    throw new ShareUrlError('decompression-failed', 'The raw DEFLATE stream is empty.')
  }
  const reader = new BitReader(data)
  let outputLength = 0
  let isFinal = false

  while (!isFinal) {
    isFinal = reader.readBits(1) === 1
    const blockType = reader.readBits(2)
    if (blockType === 0) {
      reader.alignToByte()
      const length = reader.readBits(16)
      const complement = reader.readBits(16)
      if (((length ^ 0xffff) & 0xffff) !== complement) {
        throw new ShareUrlError('decompression-failed', 'A stored DEFLATE block has an invalid length.')
      }
      outputLength = addOutput(outputLength, length, expectedLength)
      for (let index = 0; index < length; index += 1) reader.readBits(8)
    } else if (blockType === 1) {
      outputLength = readCompressedBlock(reader, fixedLiteralTable, fixedDistanceTable, outputLength, expectedLength)
    } else if (blockType === 2) {
      const { literalTable, distanceTable } = readDynamicTables(reader)
      outputLength = readCompressedBlock(reader, literalTable, distanceTable, outputLength, expectedLength)
    } else {
      throw new ShareUrlError('decompression-failed', 'The raw DEFLATE stream has a reserved block type.')
    }
  }

  if (reader.consumedBytes !== data.length) {
    throw new ShareUrlError('decompression-failed', 'The raw DEFLATE stream has trailing bytes.')
  }
  if (outputLength !== expectedLength) {
    throw new ShareUrlError('length-mismatch', `The raw DEFLATE stream produced ${outputLength} bytes, not ${expectedLength}.`)
  }
}
