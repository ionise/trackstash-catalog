import { describe, expect, it } from 'vitest'
import { countryPresentation, linkPresentation } from './labelPresentation'

describe('label presentation', () => {
  it('uses ISO country names and local flags, without guessing unsupported codes', () => {
    expect(countryPresentation('ES')).toMatchObject({ name: 'Spain', flag: expect.stringContaining('es.svg') })
    expect(countryPresentation('NZ')).toMatchObject({ name: 'New Zealand', flag: expect.stringContaining('nz.svg') })
    expect(countryPresentation('ZZ')).toEqual({ name: 'ZZ', flag: undefined })
    expect(countryPresentation('EU')).toEqual({ name: 'EU', flag: undefined })
    expect(countryPresentation(null)).toBeUndefined()
    expect(countryPresentation('')).toBeUndefined()
  })

  it('recognises actual service hosts and distinguishes link destinations', () => {
    expect(linkPresentation({ url: 'https://label.bandcamp.com/music', kind: 'other', title: 'Releases' }))
      .toEqual({ service: 'Bandcamp', label: 'Bandcamp: Releases (label.bandcamp.com/music)' })
    expect(linkPresentation({ url: 'https://ra.co/labels/1', kind: 'other', title: null }).service).toBe('Resident Advisor')
    expect(linkPresentation({ url: 'https://discogs.com.example.test', kind: 'other', title: null }).service).toBeUndefined()
    expect(linkPresentation({ url: 'https://notdiscogs.com', kind: 'other', title: null }).service).toBeUndefined()
    expect(linkPresentation({ url: 'https://example.test', kind: 'official', title: null }).label).toBe('Official website (example.test)')
    expect(linkPresentation({ url: 'https://example.test/?id=2', kind: 'other', title: null }).label).toContain('?id=2')
  })

  it('rejects unsafe URLs explicitly', () => {
    for (const url of ['javascript:alert(1)', 'https://user:pass@example.test', '/relative']) {
      expect(() => linkPresentation({ url, kind: 'other', title: null })).toThrow()
    }
  })
})
