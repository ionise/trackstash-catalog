import {
  ArrayField, ArrayInput, BooleanField, BooleanInput, Datagrid, Labeled, NumberField, NumberInput,
  SelectInput, SimpleFormIterator, TextField, TextInput, UrlField,
} from 'react-admin'
import { Alert, Typography } from '@mui/material'

const linkKinds = [
  { id: 'official', name: 'Official website' }, { id: 'bandcamp', name: 'Bandcamp' },
  { id: 'discogs', name: 'Discogs' }, { id: 'musicbrainz', name: 'MusicBrainz' },
  { id: 'resident_advisor', name: 'Resident Advisor' }, { id: 'beatport', name: 'Beatport' },
  { id: 'social', name: 'Social profile' }, { id: 'other', name: 'Other' },
]
const lifecycle = ['unknown', 'active', 'inactive', 'closed'].map(id => ({ id, name: id }))
const review = ['unreviewed', 'in_progress', 'reviewed'].map(id => ({ id, name: id.replace('_', ' ') }))
const url = (value: string | null | undefined) => {
  if (!value) return undefined
  try {
    const parsed = new URL(value)
    if (['https:', 'http:'].includes(parsed.protocol) && !parsed.username && !parsed.password) return undefined
  } catch { /* Report malformed URLs through the form validator. */ }
  return 'Use an absolute HTTP(S) URL without credentials.'
}
const requiredUrl = (value: string) => value?.trim() ? url(value) : 'A URL is required.'

export function LabelProfileFields() {
  return <>
    <Typography variant="h6">Label information</Typography>
    <TextInput source="profile.description" label="Description" multiline minRows={3} fullWidth />
    <TextInput source="profile.countryCode" label="Country code" helperText="Two-letter country code, e.g. ES or NZ (format checked only)." parse={value => value ? String(value).toUpperCase() : null}
      validate={value => !value || /^[A-Z]{2}$/.test(value) ? undefined : 'Use two uppercase letters.'} />
    <TextInput source="profile.city" label="City" />
    <NumberInput source="profile.foundedYear" label="Founded year" min={1} max={9999} step={1} />
    <SelectInput source="profile.lifecycleStatus" label="Lifecycle status" choices={lifecycle} />
    <Typography variant="h6">Personal metadata</Typography>
    <TextInput source="profile.notes" label="Personal notes" multiline minRows={3} fullWidth />
    <BooleanInput source="profile.favourite" label="Favourite" />
    <SelectInput source="profile.reviewStatus" label="Review status" choices={review} />
    <Typography variant="h6">Web links</Typography>
    <ArrayInput source="profile.links" label="Links">
      <SimpleFormIterator disableReordering sx={{ '& .RaSimpleFormIterator-action': { visibility: 'visible' } }}>
        <SelectInput source="kind" label="Link kind" choices={linkKinds} defaultValue="other" />
        <TextInput source="url" label="URL" validate={requiredUrl} fullWidth />
        <TextInput source="title" label="Link title" />
        <TextInput source="sourceUrl" label="Source URL" validate={url} helperText="Where this link was verified (optional)." fullWidth />
      </SimpleFormIterator>
    </ArrayInput>
    <Alert severity="info">Manual entries are saved with revision history. Automated enrichment, artwork uploads, and relationship editing are not enabled yet.</Alert>
  </>
}

export function LabelProfileShow() {
  return <>
    <Typography variant="h6">Label information</Typography>
    <Labeled label="Description"><TextField source="profile.description" emptyText="Not recorded" sx={{ whiteSpace: 'pre-wrap' }} /></Labeled>
    <Labeled label="Country code"><TextField source="profile.countryCode" emptyText="Not recorded" /></Labeled>
    <Labeled label="City"><TextField source="profile.city" emptyText="Not recorded" /></Labeled>
    <Labeled label="Founded year"><NumberField source="profile.foundedYear" options={{ useGrouping: false }} emptyText="Not recorded" /></Labeled>
    <Labeled label="Lifecycle status"><TextField source="profile.lifecycleStatus" /></Labeled>
    <Typography variant="h6">Personal metadata</Typography>
    <Labeled label="Personal notes"><TextField source="profile.notes" emptyText="Not recorded" sx={{ whiteSpace: 'pre-wrap' }} /></Labeled>
    <Labeled label="Favourite"><BooleanField source="profile.favourite" /></Labeled>
    <Labeled label="Review status"><TextField source="profile.reviewStatus" /></Labeled>
    <Typography variant="h6">Web links</Typography>
    <ArrayField source="profile.links">
      <Datagrid bulkActionButtons={false} rowClick={false}>
        <TextField source="kind" sortable={false} />
        <TextField source="title" sortable={false} />
        <UrlField source="url" target="_blank" rel="noopener noreferrer" sortable={false} />
        <UrlField source="sourceUrl" label="Source" target="_blank" rel="noopener noreferrer" sortable={false} />
      </Datagrid>
    </ArrayField>
  </>
}
