import countries from 'flag-icons/country.json'
import type { ArtistDetail, ArtistProfile, LabelRecord, ReleaseDetail, ReleaseCredit } from './catalog'

export function trackContributors(placement: ReleaseCredit[], recording: ReleaseCredit[]) {
  const seen = new Set<string>()
  return [...placement, ...recording].filter(credit => {
    const key = JSON.stringify([credit.artistId, credit.role, credit.creditedName])
    if (seen.has(key)) return false
    seen.add(key); return true
  })
}

export function labelLinks(record: LabelRecord) {
  return beatportLinks('label', record.name, record.profile?.links ?? [], record.externalReferences ?? [])
}
export function artistLinks(record: ArtistDetail, profile?: ArtistProfile) {
  return beatportLinks('artist', record.name, profile?.links ?? [], record.externalReferences)
}
export function releaseLinks(record: ReleaseDetail) {
  return beatportLinks('release', record.title, [], record.externalReferences)
}
export function recordingLinks(record: { title: string | null; externalReferences: { source: string; externalId: string; isPrimary: boolean }[] }) {
  return beatportLinks('track', record.title, [], record.externalReferences)
}
function beatportLinks(entity: 'label' | 'artist' | 'release' | 'track', name: string | null, curated: ArtistProfile['links'],
  external: { source: string; externalId: string; isPrimary: boolean }[]) {
  const links = [...curated]
  const linkedIds = new Set(links.flatMap(link => {
    const url = new URL(link.url)
    if (url.hostname !== 'beatport.com' && url.hostname !== 'www.beatport.com') return []
    const match = new RegExp(`^/${entity}/[^/]+/([1-9]\\d*)/?$`).exec(url.pathname)
    return match ? [match[1]] : []
  }))
  const slug = name?.normalize('NFKD').replace(/[\u0300-\u036f]/g, '').toLowerCase()
    .replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || entity
  const references = external.filter(reference =>
    reference.source.toLowerCase() === 'beatport' && /^[1-9]\d*$/.test(reference.externalId))
    .sort((a, b) => Number(b.isPrimary) - Number(a.isPrimary) || a.externalId.localeCompare(b.externalId))
  for (const reference of references) {
    if (linkedIds.has(reference.externalId)) continue
    links.push({
      kind: 'beatport', url: `https://www.beatport.com/${entity}/${slug}/${reference.externalId}`,
      title: `${entity === 'artist' ? 'Artist' : entity === 'release' ? 'Release' : 'Label'} ${reference.externalId}${reference.isPrimary ? ' (preferred)' : ''}`, sourceUrl: null,
    })
    linkedIds.add(reference.externalId)
  }
  return links
}

const flags = import.meta.glob<string>('../node_modules/flag-icons/flags/4x3/??.svg', {
  eager: true, query: '?url&no-inline', import: 'default',
})

export function countryPresentation(code: string | null | undefined) {
  if (!code) return undefined
  const country = countries.find(country => country.iso && country.code.toUpperCase() === code)
  return country
    ? { name: country.name, flag: flags[`../node_modules/flag-icons/flags/4x3/${country.code}.svg`] }
    : { name: code, flag: undefined }
}

const services = [
  { name: 'Discogs', hosts: ['discogs.com'] },
  { name: 'MusicBrainz', hosts: ['musicbrainz.org'] },
  { name: 'Bandcamp', hosts: ['bandcamp.com'] },
  { name: 'Beatport', hosts: ['beatport.com'] },
  { name: 'SoundCloud', hosts: ['soundcloud.com'] },
  { name: 'Resident Advisor', hosts: ['ra.co', 'residentadvisor.net'] },
  { name: 'Instagram', hosts: ['instagram.com'] },
  { name: 'Facebook', hosts: ['facebook.com', 'fb.com'] },
  { name: 'YouTube', hosts: ['youtube.com', 'youtu.be'] },
  { name: 'X', hosts: ['x.com', 'twitter.com'] },
]

export function linkPresentation(link: { url: string; kind: string; title: string | null }) {
  const url = new URL(link.url)
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password)
    throw new Error('Label links must be HTTP(S) URLs without credentials.')
  const service = services.find(service => service.hosts.some(host => url.hostname === host || url.hostname.endsWith(`.${host}`)))
  const name = service?.name ?? (link.kind === 'official' ? 'Official website' : 'External link')
  return { service: service?.name, label: `${name}${link.title ? `: ${link.title}` : ''} (${url.hostname}${url.pathname === '/' ? '' : url.pathname}${url.search}${url.hash})` }
}
