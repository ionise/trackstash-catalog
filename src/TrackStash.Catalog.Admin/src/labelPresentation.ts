import countries from 'flag-icons/country.json'
import type { LabelRecord } from './catalog'

export function labelLinks(record: LabelRecord) {
  const links = [...(record.profile?.links ?? [])]
  const linkedIds = new Set(links.flatMap(link => {
    const url = new URL(link.url)
    if (url.hostname !== 'beatport.com' && url.hostname !== 'www.beatport.com') return []
    const match = /^\/label\/[^/]+\/([1-9]\d*)\/?$/.exec(url.pathname)
    return match ? [match[1]] : []
  }))
  const slug = record.name?.normalize('NFKD').replace(/[\u0300-\u036f]/g, '').toLowerCase()
    .replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'label'
  const references = (record.externalReferences ?? []).filter(reference =>
    reference.source.toLowerCase() === 'beatport' && /^[1-9]\d*$/.test(reference.externalId))
    .sort((a, b) => Number(b.isPrimary) - Number(a.isPrimary) || a.externalId.localeCompare(b.externalId))
  for (const reference of references) {
    if (linkedIds.has(reference.externalId)) continue
    links.push({
      kind: 'beatport', url: `https://www.beatport.com/label/${slug}/${reference.externalId}`,
      title: `Label ${reference.externalId}${reference.isPrimary ? ' (preferred)' : ''}`, sourceUrl: null,
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
