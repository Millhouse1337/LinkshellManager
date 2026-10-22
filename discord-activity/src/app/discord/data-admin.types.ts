// Data Admin (website only, super admins): the JSON shapes /api/data-admin answers with. They are
// the server's own Data Admin view models (ViewModels/DataAdminViewModels.cs), camel-cased.

export interface DataAdminSchema {
  databaseName: string;
  tableCount: number;
  columnCount: number;
  relationshipCount: number;
  // DataAdminSchemaGraph.Json as-is: the shape wwwroot/js/data-admin-schema.js draws.
  diagram: DataAdminDiagram;
}

export interface DataAdminDiagram {
  tables: DataAdminDiagramTable[];
  relationships: unknown[];
  junctions: unknown[];
}

export interface DataAdminDiagramTable {
  id: string;
  name: string;
  label: string;
  // Null for the Identity tables: drawn on the diagram, but they have no records page.
  slug: string | null;
  parent: string | null;
  columns: unknown[];
}

// ?q=&sort=&desc=&page=&f.<Column>= (DataAdminListQuery).
export interface DataAdminListQuery {
  search: string | null;
  sort: string | null;
  descending: boolean;
  page: number;
  filters: Record<string, string>;
}

export interface DataAdminList {
  slug: string;
  displayName: string;
  tableName: string;
  query: DataAdminListQuery;
  columns: { name: string; displayName: string; isKey: boolean; isNumeric: boolean }[];
  rows: { key: string; label: string; cells: DataAdminCell[] }[];
  activeFilters: { column: string; displayName: string; value: string; label: string }[];
  total: number;
  page: number;
  totalPages: number;
  pageSize: number;
  sortColumn: string;
  descending: boolean;
  searchHint: string;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
  firstRowNumber: number;
  lastRowNumber: number;
}

// A list cell or a details field. A foreign key to a visible table links to that row (linkSlug + linkKey).
export interface DataAdminCell {
  text: string;
  isEmpty: boolean;
  isNumeric?: boolean;
  linkSlug: string | null;
  linkKey: string | null;
}

export interface DataAdminDetailsField extends DataAdminCell {
  name: string;
  displayName: string;
  isLongText: boolean;
  // "Linkshell #5" beside a foreign key, so the raw key stays visible.
  note: string | null;
}

export interface DataAdminRelated {
  slug: string;
  displayName: string;
  foreignKeyColumn: string;
  foreignKeyDisplayName: string;
  count: number;
  // What happens to those rows when this row is deleted, in words.
  onDelete: string;
  // A policy-hidden dependent: named and counted, never linked.
  isHidden: boolean;
  // { "f.<ForeignKeyColumn>": "<key>" } for the dependent's list.
  filterRoute: Record<string, string>;
}

export interface DataAdminDetails {
  slug: string;
  displayName: string;
  tableName: string;
  key: string;
  label: string;
  fields: DataAdminDetailsField[];
  related: DataAdminRelated[];
  canEdit: boolean;
  canDelete: boolean;
  discordSideEffect: string | null;
}

export interface DataAdminField {
  name: string;
  displayName: string;
  value: string;
  inputType: 'text' | 'textarea' | 'number' | 'checkbox' | 'datetime-local' | 'select';
  isRequired: boolean;
  isInteger: boolean;
  maxLength: number | null;
  isKey: boolean;
  hint: string | null;
  error: string | null;
  options: { value: string; text: string }[] | null;
}

export interface DataAdminForm {
  slug: string;
  displayName: string;
  isCreate: boolean;
  key: string | null;
  label: string | null;
  fields: DataAdminField[];
  // Errors not tied to one field (a database refusal).
  formErrors: string[];
  discordSideEffect: string | null;
}

export interface DataAdminImpactNode {
  displayName: string;
  slug: string;
  foreignKeyDisplayName: string;
  effect: 'deleted' | 'unlinked' | 'blocked';
  count: number;
  alreadyCounted: number;
  overflow: boolean;
  depthCapped: boolean;
  children: DataAdminImpactNode[];
}

export interface DataAdminDeletePreview {
  slug: string;
  displayName: string;
  key: string;
  label: string;
  isBlocked: boolean;
  isTruncated: boolean;
  deletedRows: number;
  discordSideEffect: string | null;
  children: DataAdminImpactNode[];
  totals: { displayName: string; rows: number }[];
}

export interface DataAdminSaved {
  key: string;
  message: string;
}
