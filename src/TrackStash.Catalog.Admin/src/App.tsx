import { useState } from 'react'
import { Admin, AppBar, Layout, Logout, Menu, Resource, UserMenu, memoryStore, useLogout, useNotify, useSidebarState } from 'react-admin'
import type { AppBarProps, LayoutProps } from 'react-admin'
import { ListItemIcon, ListItemText, MenuItem } from '@mui/material'
import { authProvider, dataProvider } from './catalog'
import { LabelCreate, LabelEdit, LabelList, LabelShow } from './labels'
import { LoginPage } from './LoginPage'
import { Backups } from './Backups'
import { PurchaseReleases } from './PurchaseReleases'
import { BeatportBrowse } from './BeatportBrowse'
import { MediaInventory, MediaMatching } from './Media'
import { MediaLibraries } from './MediaLibraries'
import { RemoteInventory } from './RemoteInventory'
import TravelExploreIcon from '@mui/icons-material/TravelExplore'
import { ReleaseList, ReleaseShow } from './Releases'
import { ArtistList, ArtistShow } from './Artists'
import { RecordingShow, TrackListPage } from './Recordings'
import QueueMusicIcon from '@mui/icons-material/QueueMusic'
import SearchIcon from '@mui/icons-material/Search'
import { LabelGroupList, LabelGroupCreate, LabelGroupShow, LabelGroupEdit } from './LabelGroups'
import BackupIcon from '@mui/icons-material/Backup'
import SettingsIcon from '@mui/icons-material/Settings'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'

const store = memoryStore()

function CatalogMenu() {
  const [expanded, setExpanded] = useState(true)
  const [sidebarOpen, setSidebarOpen] = useSidebarState()
  return <Menu>
    <Menu.ResourceItem name="labels" />
    <Menu.ResourceItem name="releases" />
    <Menu.ResourceItem name="artists" />
    <Menu.ResourceItem name="tracks" />
    <Menu.Item to="/media" primaryText="Media" leftIcon={<QueueMusicIcon />} />
    <Menu.Item to="/media-matches" primaryText="Media matching" leftIcon={<QueueMusicIcon />} />
    <Menu.ResourceItem name="label-groups" />
    <Menu.Item to="/purchase-releases" primaryText="Purchase releases" leftIcon={<SearchIcon />} />
    <Menu.Item to="/beatport" primaryText="Beatport" leftIcon={<TravelExploreIcon />} />
    <MenuItem aria-label="System" aria-expanded={sidebarOpen && expanded} aria-controls="system-backup-menu" onClick={() => {
      if (!sidebarOpen) { setSidebarOpen(true); setExpanded(true) }
      else setExpanded(!expanded)
    }}>
      <ListItemIcon><SettingsIcon /></ListItemIcon>
      {sidebarOpen && <><ListItemText primary="System" />{expanded ? <ExpandLessIcon /> : <ExpandMoreIcon />}</>}
    </MenuItem>
    {sidebarOpen && expanded && <Menu.Item id="system-backup-menu" to="/backups" primaryText="Backup" leftIcon={<BackupIcon />} sx={{ pl: 4 }} />}
    {sidebarOpen && expanded && <Menu.Item to="/media-libraries" primaryText="Libraries" leftIcon={<QueueMusicIcon />} sx={{ pl: 4 }} />}
  </Menu>
}

function CatalogLogout() {
  const logout = useLogout()
  const notify = useNotify()
  const [pending, setPending] = useState(false)
  return <Logout disabled={pending} onClick={async () => {
    setPending(true)
    try {
      await logout({}, undefined, false)
    } catch (error) {
      notify(error instanceof Error ? error.message : 'Could not confirm sign-out. Try again.', { type: 'error' })
    } finally {
      setPending(false)
    }
  }} />
}

function CatalogAppBar(props: AppBarProps) {
  return <AppBar {...props} userMenu={<UserMenu><CatalogLogout /></UserMenu>} />
}

function CatalogLayout(props: LayoutProps) {
  return <Layout {...props} appBar={CatalogAppBar} menu={CatalogMenu} />
}

export default function App() {
  return (
    <Admin
      title="TrackStash Catalog"
      authProvider={authProvider}
      dataProvider={dataProvider}
      loginPage={LoginPage}
      disableTelemetry
      store={store}
      layout={CatalogLayout}
    >
      <Resource name="labels" list={LabelList} show={LabelShow} create={LabelCreate} edit={LabelEdit} recordRepresentation="name" />
      <Resource name="releases" list={ReleaseList} show={ReleaseShow} recordRepresentation="title" />
      <Resource name="artists" list={ArtistList} show={ArtistShow} recordRepresentation="name" />
      <Resource name="tracks" list={TrackListPage} icon={QueueMusicIcon} recordRepresentation="title" />
      <Resource name="recordings" show={RecordingShow} recordRepresentation="title" />
      <Resource name="label-groups" options={{ label: 'Label groups' }} list={LabelGroupList} show={LabelGroupShow} create={LabelGroupCreate} edit={LabelGroupEdit} recordRepresentation="name" />
      <Resource name="backups" list={Backups} icon={BackupIcon} />
      <Resource name="purchase-releases" list={PurchaseReleases} />
      <Resource name="beatport" list={BeatportBrowse} />
      <Resource name="media" list={MediaInventory} />
      <Resource name="remote-inventory" list={RemoteInventory} />
      <Resource name="media-matches" list={MediaMatching} />
      <Resource name="media-libraries" list={MediaLibraries} />
    </Admin>
  )
}
