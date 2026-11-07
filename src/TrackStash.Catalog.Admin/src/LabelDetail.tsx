import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { EntityBeatportBrowse } from './BeatportBrowse'
import { beatportIdentities } from './catalog'
import { LabelArtists } from './Artists'
import type { ReactNode } from 'react'
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Chip, Divider,
  IconButton, Link, Stack, SvgIcon, Tab, Tabs, Tooltip, Typography,
} from '@mui/material'
import CameraAltIcon from '@mui/icons-material/CameraAlt'
import FavoriteIcon from '@mui/icons-material/Favorite'
import FavoriteBorderIcon from '@mui/icons-material/FavoriteBorder'
import LanguageIcon from '@mui/icons-material/Language'
import LinkIcon from '@mui/icons-material/Link'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import ImageOutlinedIcon from '@mui/icons-material/ImageOutlined'
import { siBandcamp, siBeatport, siDiscogs, siMusicbrainz, siSoundcloud } from 'simple-icons'
import {
  ArrayField, BooleanField, Datagrid, DateField, EditButton, SimpleShowLayout, TextField, UrlField,
  useLogoutIfAccessDenied, useRecordContext, useRefresh, useUpdate,
} from 'react-admin'
import { ArtworkImage, LabelArtwork } from './LabelArtwork'
import type { LabelArtworkState } from './LabelArtwork'
import type { LabelRecord } from './catalog'
import { LabelMerge } from './LabelMerge'
import { LabelRelationships } from './LabelRelationships'
import { LabelGroupSections } from './LabelGroups'
import { LabelReleases } from './Releases'
import { LocalTrackList } from './TrackList'
import { countryPresentation, labelLinks, linkPresentation } from './labelPresentation'

const serviceIcons = new Map([
  ['Bandcamp', siBandcamp], ['Beatport', siBeatport], ['Discogs', siDiscogs],
  ['MusicBrainz', siMusicbrainz], ['SoundCloud', siSoundcloud],
])

function Favourite({ record }: { record: LabelRecord }) {
  const [update] = useUpdate<LabelRecord>()
  const refresh = useRefresh()
  const logout = useLogoutIfAccessDenied()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  async function toggle() {
    if (!record.profile) { setError('Reload this label to load its profile before changing Favourite.'); return }
    setBusy(true); setError('')
    try {
      await update('labels', {
        id: record.id, previousData: record,
        data: { ...record, profile: { ...record.profile, favourite: !record.profile.favourite } },
      }, { mutationMode: 'pessimistic', returnPromise: true })
      refresh()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Favourite could not be saved.')
      await logout(failure)
    } finally { setBusy(false) }
  }
  return <Box>
    <Button variant="outlined" aria-pressed={record.profile?.favourite ?? false} disabled={busy || Boolean(error)}
      startIcon={record.profile?.favourite ? <FavoriteIcon /> : <FavoriteBorderIcon />} onClick={() => void toggle()}>
      Favourite
    </Button>
    {error && <Alert severity="error" sx={{ mt: 1 }} action={<Button onClick={() => { setError(''); refresh() }}>Reload label</Button>}>{error}</Alert>}
  </Box>
}

function Header({ record, artwork }: { record: LabelRecord; artwork: LabelArtworkState }) {
  const [, setParams] = useSearchParams()
  const hasBeatportIdentity = beatportIdentities(record.externalReferences).length > 0
  const preferred = artwork.gallery?.items.find(item => item.isPreferred)
  const country = countryPresentation(record.profile?.countryCode)
  const profile = record.profile
  return <Stack component="header" direction={{ xs: 'column', sm: 'row' }} spacing={3}>
    <Box sx={{ position: 'relative', width: 184, minWidth: 184, minHeight: 184, alignSelf: 'flex-start',
      '& .logo-action': { opacity: 0 }, '&:hover .logo-action, &:focus-within .logo-action': { opacity: 1 },
      '@media (hover: none)': { '& .logo-action': { opacity: 1 } } }}>
      {preferred
        ? <ArtworkImage assetId={preferred.assetId} caption={`${record.name ?? 'Label'} logo`} size={184} />
        : <Stack spacing={1} sx={{ height: 184, borderRadius: 2, bgcolor: 'action.hover', alignItems: 'center', justifyContent: 'center', p: 2 }}>
          <ImageOutlinedIcon fontSize="large" color="disabled" />
          <Typography variant="body2" sx={{ textAlign: 'center' }}>{artwork.gallery ? 'No preferred artwork' : artwork.error ? 'Artwork unavailable' : 'Loading artwork...'}</Typography>
        </Stack>}
      <Box className="logo-action" sx={{ position: 'absolute', right: 8, bottom: 8 }}>
        <Tooltip title="Change label artwork">
          <span>
            <IconButton aria-label="Change label artwork" disabled={artwork.busy} onClick={artwork.manage}
              sx={{ bgcolor: 'background.paper', boxShadow: 2, '&:hover': { bgcolor: 'background.default' } }}>
              <CameraAltIcon />
            </IconButton>
          </span>
        </Tooltip>
      </Box>
    </Box>
    <Stack spacing={2} sx={{ flex: 1, minWidth: 0 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start', justifyContent: 'space-between' }}>
        <Typography component="h1" variant="h4" sx={{ overflowWrap: 'anywhere', fontWeight: 600 }}>{record.name || 'Unnamed label'}</Typography>
        <Stack direction="row" useFlexGap sx={{ flexWrap: 'wrap', justifyContent: 'flex-end' }}>
          {hasBeatportIdentity && <Button size="small" onClick={() => setParams(current => {
            const next = new URLSearchParams(current); next.set('tab', 'Beatport'); return next
          })}>Browse on Beatport</Button>}
          <EditButton /><LabelMerge />
        </Stack>
      </Stack>
      <Stack direction="row" useFlexGap spacing={1} sx={{ flexWrap: 'wrap', alignItems: 'center' }}>
        <Favourite record={record} />
        {labelLinks(record).map(link => {
          const presentation = linkPresentation(link)
          const icon = serviceIcons.get(presentation.service ?? '')
          return <Tooltip title={presentation.label} key={`${link.kind}/${link.url}`}>
            <IconButton component={Link} href={link.url} target="_blank" rel="noopener noreferrer" aria-label={presentation.label}>
              {icon
                ? <SvgIcon viewBox="0 0 24 24"><path d={icon.path} /></SvgIcon>
                : link.kind === 'official' ? <LanguageIcon /> : <LinkIcon />}
            </IconButton>
          </Tooltip>
        })}
      </Stack>
      <Stack direction="row" useFlexGap spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        {country && <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
          {country.flag && <Box component="img" src={country.flag} alt="" aria-hidden="true" sx={{ width: 20, height: 15, objectFit: 'contain' }} />}
          <Typography>{country.name}</Typography>
        </Stack>}
        {profile?.city && <Typography>{country ? ' / ' : ''}{profile.city}</Typography>}
        {profile?.foundedYear && <Typography>{country || profile.city ? ' / ' : ''}Founded {profile.foundedYear}</Typography>}
        {profile?.lifecycleStatus && <Chip size="small" variant="outlined" label={`Status: ${profile.lifecycleStatus}`} />}
      </Stack>
    </Stack>
  </Stack>
}

const sections = ['Releases', 'Artists', 'Tracks', 'Details', 'Relationships', 'Beatport']

function RelatedContent({ children }: { children: ReactNode }) {
  const [params, setParams] = useSearchParams()
  const selected = sections.indexOf(params.get('tab') ?? 'Releases')
  const tab = selected < 0 ? 0 : selected
  const record = useRecordContext<LabelRecord>()
  return <Box component="section" aria-label="Related content" sx={{ minWidth: 0, width: '100%' }}>
    <Tabs value={tab} onChange={(_, value: number) => setParams(current => {
      const next = new URLSearchParams(current); next.set('tab', sections[value]); return next
    })} aria-label="Label related content"
      variant="scrollable" scrollButtons="auto" allowScrollButtonsMobile
      sx={{ minWidth: 0, maxWidth: '100%', width: '100%', contain: 'inline-size' }}>
      {sections.map((name, index) => <Tab key={name} id={`label-tab-${index}`} aria-controls={`label-panel-${index}`}
        label={name} sx={{ minWidth: 0, px: { xs: 1.5, sm: 2 } }} />)}
    </Tabs>
    {sections.map((name, index) => <Box key={name} role="tabpanel" hidden={tab !== index}
      id={`label-panel-${index}`} aria-labelledby={`label-tab-${index}`} sx={{ py: 3, minWidth: 0 }}>
      {name === 'Releases' ? tab === index && record && <LabelReleases key={record.id} labelId={record.id} /> : name === 'Artists' ? tab === index && record && <LabelArtists key={record.id} labelId={record.id} /> : name === 'Tracks' ? tab === index && record && <LocalTrackList key={record.id} filter={{ labelId: record.id }} context="label" label="Label tracks" /> : name === 'Beatport' ? tab === index && record && <EntityBeatportBrowse key={record.id} kind="label" name={record.name} externalReferences={record.externalReferences} /> : name === 'Details' ? children : <Stack spacing={3} sx={{ minWidth: 0 }}>
        <LabelRelationships />
        <LabelGroupSections />
      </Stack>}
    </Box>)}
  </Box>
}

export function LabelDetail() {
  const record = useRecordContext<LabelRecord>()
  if (!record) return null
  return <LabelArtwork>{(artwork, gallery) => <Stack spacing={3} sx={{ p: { xs: 2, md: 4 }, minWidth: 0 }}>
    <Header record={record} artwork={artwork} />
    <Box component="section" aria-label="Description">
      <Typography sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', maxWidth: '80ch', lineHeight: 1.8 }} color={record.profile?.description ? 'text.primary' : 'text.secondary'}>
        {record.profile?.description || 'No description recorded.'}
      </Typography>
    </Box>
    <Divider />
    <RelatedContent key={record.id}>
    <Stack spacing={3} sx={{ minWidth: 0 }}>
    <Box component="section" aria-label="Artwork gallery">{gallery}</Box>
    <Box component="section" aria-label="Personal notes">
      <Typography variant="h6" component="h2" gutterBottom>Personal notes</Typography>
      <Typography sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', mb: 2 }}>{record.profile?.notes || 'No personal notes recorded.'}</Typography>
      <Chip variant="outlined" label={`Review: ${record.profile?.reviewStatus.replaceAll('_', ' ') ?? 'unreviewed'}`} />
    </Box>
    <Accordion defaultExpanded>
      <AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography>Record details</Typography></AccordionSummary>
      <AccordionDetails sx={{ minWidth: 0, overflowX: 'auto' }}>
        <SimpleShowLayout sx={{ '& .MuiTypography-root': { overflowWrap: 'anywhere' } }}>
          <TextField source="sortName" emptyText="Not recorded" />
          <TextField source="id" label="Catalog ID" />
          <DateField source="createdUtc" showTime />
          <DateField source="updatedUtc" showTime />
          <ArrayField source="aliases">
            <Datagrid bulkActionButtons={false} rowClick={false} empty={<Typography color="text.secondary">No aliases recorded.</Typography>}>
              <TextField source="value" sortable={false} />
              <BooleanField source="isPrimary" sortable={false} />
            </Datagrid>
          </ArrayField>
          <ArrayField source="externalReferences">
            <Datagrid bulkActionButtons={false} rowClick={false} empty={<Typography color="text.secondary">No provider references recorded.</Typography>}>
              <TextField source="source" sortable={false} />
              <TextField source="externalId" label="Provider ID" sortable={false} />
              <BooleanField source="isPrimary" sortable={false} />
              <DateField source="lastSeenUtc" showTime sortable={false} />
            </Datagrid>
          </ArrayField>
          <ArrayField source="profile.links" label="Link sources">
            <Datagrid bulkActionButtons={false} rowClick={false} empty={<Typography color="text.secondary">No link sources recorded.</Typography>}>
              <TextField source="kind" sortable={false} />
              <TextField source="title" sortable={false} />
              <UrlField source="url" target="_blank" rel="noopener noreferrer" sortable={false} />
              <UrlField source="sourceUrl" label="Verified from" target="_blank" rel="noopener noreferrer" sortable={false} />
            </Datagrid>
          </ArrayField>
        </SimpleShowLayout>
      </AccordionDetails>
    </Accordion>
    </Stack>
    </RelatedContent>
  </Stack>}</LabelArtwork>
}
