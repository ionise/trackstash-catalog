import { useCallback } from 'react'
import { useSearchParams } from 'react-router-dom'

export const trackPageSize = 25

/** Where a track list is shown; each context is the same list with a different filter and default columns. */
export type TrackContext = 'all' | 'release' | 'label' | 'artist' | 'recording'
export interface TrackFilter { releaseId?: string; labelId?: string; artistId?: string; recordingId?: string }
export interface TrackView { page: number; q: string; sort: string }

export const trackSorts = [
  { value: 'date_desc', label: 'Newest release first' },
  { value: 'date_asc', label: 'Oldest release first' },
  { value: 'title', label: 'Title A–Z' },
  { value: 'artist', label: 'Artist A–Z' },
  { value: 'release', label: 'Release A–Z' },
  { value: 'order', label: 'Release order' },
] as const

export function defaultTrackSort(context: TrackContext) { return context === 'release' ? 'order' : 'date_desc' }

export function trackQuery(filter: TrackFilter, view: TrackView, groupByRecording = false) {
  const query = new URLSearchParams({ page: String(view.page), pageSize: String(trackPageSize) })
  for (const [key, value] of Object.entries(filter)) if (value) query.set(key, value)
  if (view.q.trim()) query.set('q', view.q.trim())
  const [sort, order] = view.sort.split('_')
  if (sort) query.set('sort', sort)
  if (order) query.set('order', order)
  if (groupByRecording) query.set('groupBy', 'recording')
  return query
}

export function formatDuration(ms: number | null) {
  return ms === null ? '—' : `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, '0')}`
}

const paramName = (prefix: string, key: string) => prefix ? `${prefix}${key[0].toUpperCase()}${key.slice(1)}` : key

/** Track list state kept in the URL so views can be bookmarked and survive navigation. */
export function useUrlTrackView(prefix = '', fallbackSort = defaultTrackSort('all')): [TrackView, (view: TrackView) => void] {
  const [params, setParams] = useSearchParams()
  const number = Number(params.get(paramName(prefix, 'page')) ?? 1)
  const sort = params.get(paramName(prefix, 'sort')) ?? fallbackSort
  const view = {
    page: Number.isSafeInteger(number) && number > 0 ? number : 1,
    q: params.get(paramName(prefix, 'q')) ?? '',
    sort: trackSorts.some(s => s.value === sort) ? sort : fallbackSort,
  }
  const change = useCallback((next: TrackView) => setParams(current => {
    const updated = new URLSearchParams(current)
    updated.set(paramName(prefix, 'page'), String(next.page))
    for (const [key, value, empty] of [['q', next.q, ''], ['sort', next.sort, fallbackSort]] as const)
      if (value === empty) updated.delete(paramName(prefix, key)); else updated.set(paramName(prefix, key), value)
    return updated
  }, { replace: true }), [setParams, prefix, fallbackSort])
  return [view, change]
}
