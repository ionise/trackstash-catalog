import { describe, expect, it } from 'vitest'
import { trackContributors } from './labelPresentation'
import type { ReleaseCredit } from './catalog'

describe('track presentation credits', () => {
  const primary: ReleaseCredit = { artistId: 'a', artistName: 'Artist', creditedName: 'Spelling', role: 'primary', position: 1, identityStatus: 'accepted' }
  it('deduplicates matching identity/role/spelling across scopes while retaining order and distinct evidence', () => {
    const remix = { ...primary, artistId: 'b', role: 'remixer', identityStatus: 'unresolved_compound' }
    const differentSpelling = { ...primary, creditedName: 'Other spelling' }
    const anotherIdentity = { ...primary, artistId: 'c' }
    expect(trackContributors([primary, remix], [{ ...primary, position: 2 }, differentSpelling, anotherIdentity, remix]))
      .toEqual([primary, remix, differentSpelling, anotherIdentity])
  })
  it('retains Recording-only Artists and distinct roles without mutating inputs', () => {
    const composer = { ...primary, role: 'composer' }
    expect(trackContributors([], [primary, composer])).toEqual([primary, composer])
    expect(trackContributors([primary], [])).toEqual([primary])
  })
})
