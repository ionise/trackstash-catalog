import { describe, expect, it } from 'vitest'
import { formatLength } from './beatportFormat'

describe('Beatport track length', () => {
  it('formats milliseconds as minutes and seconds', () => {
    expect(formatLength(null)).toBe('')
    expect(formatLength(-1)).toBe('')
    expect(formatLength(0)).toBe('0:00')
    expect(formatLength(123456)).toBe('2:03')
    expect(formatLength(3600000)).toBe('60:00')
  })
})
