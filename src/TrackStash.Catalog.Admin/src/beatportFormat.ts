export function formatLength(ms: number | null) {
  if (ms === null || ms < 0) return ''
  const seconds = Math.round(ms / 1000)
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}
