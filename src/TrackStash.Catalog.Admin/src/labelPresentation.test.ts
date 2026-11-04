import { describe, expect, it } from 'vitest'
import { countryPresentation, labelLinks, linkPresentation } from './labelPresentation'

describe('label presentation', () => {
  it('derives distinct Beatport links preferred first without mutating curated data', () => {
    const record = {
      id: 'one', name: 'Univäck Records', sortName: null, version: 'v1',
      externalReferences: [
        { source: 'beatport', externalId: '254639', isPrimary: false },
        { source: 'beatport', externalId: '10955', isPrimary: true },
        { source: 'beatport', externalId: '10955', isPrimary: false },
        { source: 'beatport', externalId: '../bad', isPrimary: false },
        { source: 'discogs', externalId: '999', isPrimary: false },
      ],
    }
    expect(labelLinks(record).map(link => link.url)).toEqual([
      'https://www.beatport.com/label/univack-records/10955',
      'https://www.beatport.com/label/univack-records/254639',
    ])
    expect(labelLinks({ ...record, name: null })[0].url).toContain('/label/label/10955')
    expect(labelLinks({ ...record, externalReferences: undefined })).toEqual([])
  })

  it('keeps a manually supplied Beatport URL in preference to a generated URL for the same ID', () => {
    const link = { kind: 'beatport', url: 'https://www.beatport.com/label/custom-name/10955?ref=test', title: 'Curated', sourceUrl: null }
    const record = {
      id: 'one', name: 'Label', sortName: null, version: 'v1',
      profile: { description: null, countryCode: null, city: null, foundedYear: null, lifecycleStatus: 'unknown',
        notes: null, favourite: false, reviewStatus: 'unreviewed', links: [link] },
      externalReferences: [{ source: 'beatport', externalId: '10955', isPrimary: true }],
    }
    expect(labelLinks(record)).toEqual([link])
    expect(record.profile.links).toEqual([link])
  })
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
