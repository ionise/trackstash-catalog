import { Admin, Resource, memoryStore } from 'react-admin'
import { authProvider, dataProvider } from './catalog'
import { LabelCreate, LabelEdit, LabelList, LabelShow } from './labels'
import { LoginPage } from './LoginPage'

const store = memoryStore()

export default function App() {
  return (
    <Admin
      title="TrackStash Catalog"
      authProvider={authProvider}
      dataProvider={dataProvider}
      loginPage={LoginPage}
      disableTelemetry
      store={store}
    >
      <Resource name="labels" list={LabelList} show={LabelShow} create={LabelCreate} edit={LabelEdit} recordRepresentation="name" />
    </Admin>
  )
}
