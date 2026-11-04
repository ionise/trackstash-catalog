import {
  ArrayField, BooleanField, Create, Datagrid, DateField, Edit, EditButton,
  List, Pagination, required, Show, ShowButton, SimpleForm, SimpleShowLayout,
  TextField, TextInput, Toolbar, SaveButton,
} from 'react-admin'
import { LabelMerge } from './LabelMerge'
import { LabelProfileFields, LabelProfileShow } from './LabelProfile'

const nameValidators = [required(), (value: string) => value?.trim() ? undefined : 'A nonblank name is required.']
const filters = [<TextInput key="q" source="q" label="Search names and aliases" alwaysOn />]
const LabelPagination = () => <Pagination rowsPerPageOptions={[10, 25, 50, 100]} />
const LabelToolbar = () => <Toolbar><SaveButton /></Toolbar>

export function LabelList() {
  return (
    <List filters={filters} sort={{ field: 'name', order: 'ASC' }} perPage={25} pagination={<LabelPagination />} exporter={false}>
      <Datagrid rowClick="show" bulkActionButtons={false}>
        <TextField source="name" />
        <TextField source="sortName" />
        <TextField source="id" label="Catalog ID" />
        <ShowButton />
        <EditButton />
      </Datagrid>
    </List>
  )
}

export function LabelShow() {
  return (
    <Show>
      <SimpleShowLayout>
        <LabelMerge />
        <TextField source="name" />
        <TextField source="sortName" />
        <TextField source="id" label="Catalog ID" />
        <DateField source="createdUtc" showTime />
        <DateField source="updatedUtc" showTime />
        <LabelProfileShow />
        <ArrayField source="aliases">
          <Datagrid bulkActionButtons={false} rowClick={false}>
            <TextField source="value" sortable={false} />
            <BooleanField source="isPrimary" sortable={false} />
          </Datagrid>
        </ArrayField>
        <ArrayField source="externalReferences">
          <Datagrid bulkActionButtons={false} rowClick={false}>
            <TextField source="source" sortable={false} />
            <TextField source="externalId" label="Provider ID" sortable={false} />
            <BooleanField source="isPrimary" sortable={false} />
            <DateField source="lastSeenUtc" showTime sortable={false} />
          </Datagrid>
        </ArrayField>
      </SimpleShowLayout>
    </Show>
  )
}

export function LabelCreate() {
  return (
    <Create redirect="show">
      <SimpleForm toolbar={<LabelToolbar />}>
        <TextInput source="name" validate={nameValidators} fullWidth />
        <TextInput source="sortName" fullWidth />
      </SimpleForm>
    </Create>
  )
}

export function LabelEdit() {
  return (
    <Edit mutationMode="pessimistic" redirect="show" queryOptions={{ refetchOnWindowFocus: false }}>
      <SimpleForm toolbar={<LabelToolbar />}>
        <TextField source="id" label="Catalog ID" />
        <TextInput source="name" validate={nameValidators} fullWidth />
        <TextInput source="sortName" fullWidth />
        <LabelProfileFields />
      </SimpleForm>
    </Edit>
  )
}
