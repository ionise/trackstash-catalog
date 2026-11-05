import { useState } from 'react'
import { Admin, Layout, Menu, Resource, memoryStore, useSidebarState } from 'react-admin'
import type { LayoutProps } from 'react-admin'
import { ListItemIcon, ListItemText, MenuItem } from '@mui/material'
import { authProvider, dataProvider } from './catalog'
import { LabelCreate, LabelEdit, LabelList, LabelShow } from './labels'
import { LoginPage } from './LoginPage'
import { Backups } from './Backups'
import { PurchaseReleases } from './PurchaseReleases'
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
    <Menu.ResourceItem name="label-groups" />
    <Menu.Item to="/purchase-releases" primaryText="Purchase releases" leftIcon={<SearchIcon />} />
    <MenuItem aria-label="System" aria-expanded={sidebarOpen && expanded} aria-controls="system-backup-menu" onClick={() => {
      if (!sidebarOpen) { setSidebarOpen(true); setExpanded(true) }
      else setExpanded(!expanded)
    }}>
      <ListItemIcon><SettingsIcon /></ListItemIcon>
      {sidebarOpen && <><ListItemText primary="System" />{expanded ? <ExpandLessIcon /> : <ExpandMoreIcon />}</>}
    </MenuItem>
    {sidebarOpen && expanded && <Menu.Item id="system-backup-menu" to="/backups" primaryText="Backup" leftIcon={<BackupIcon />} sx={{ pl: 4 }} />}
  </Menu>
}

function CatalogLayout(props: LayoutProps) {
  return <Layout {...props} menu={CatalogMenu} />
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
      <Resource name="label-groups" options={{ label: 'Label groups' }} list={LabelGroupList} show={LabelGroupShow} create={LabelGroupCreate} edit={LabelGroupEdit} recordRepresentation="name" />
      <Resource name="backups" list={Backups} icon={BackupIcon} />
      <Resource name="purchase-releases" list={PurchaseReleases} />
    </Admin>
  )
}
