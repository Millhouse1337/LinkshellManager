import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { DataAdminDeleteComponent } from './data-admin-delete.component';
import { DataAdminDetailsComponent } from './data-admin-details.component';
import { DataAdminFormComponent } from './data-admin-form.component';
import { DataAdminIndexComponent } from './data-admin-index.component';
import { DataAdminListComponent } from './data-admin-list.component';

// Website only, super admins only: the database browser that used to be the Razor /data-admin
// pages. Its own little router over the path after /data-admin/ (ActivityHomeComponent hands the
// segments in and turns `navigate` back into the URL):
//   []                    the landing: every table (and the schema diagram)
//   [slug]                a table's rows
//   [slug, 'create']      a new row
//   [slug, id]            one row, and the rows pointing at it
//   [slug, id, 'edit']    edit it
//   [slug, id, 'delete']  what deleting it would take with it, then delete
@Component({
  selector: 'app-data-admin-tab',
  imports: [DataAdminDeleteComponent, DataAdminDetailsComponent, DataAdminFormComponent, DataAdminIndexComponent, DataAdminListComponent],
  template: `
    <div class="panel-tab fade">
      @switch (kind()) {
        @case ('index') { <app-data-admin-index (navigate)="navigate.emit($event)" /> }
        @case ('list') { <app-data-admin-list [slug]="slug()" (navigate)="navigate.emit($event)" /> }
        @case ('create') { <app-data-admin-form [slug]="slug()" [id]="null" (navigate)="navigate.emit($event)" /> }
        @case ('details') { <app-data-admin-details [slug]="slug()" [id]="id()" (navigate)="navigate.emit($event)" /> }
        @case ('edit') { <app-data-admin-form [slug]="slug()" [id]="id()" (navigate)="navigate.emit($event)" /> }
        @case ('delete') { <app-data-admin-delete [slug]="slug()" [id]="id()" (navigate)="navigate.emit($event)" /> }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DataAdminTabComponent {
  readonly path = input.required<readonly string[]>();
  readonly navigate = output<string[]>();

  protected readonly slug = computed(() => this.path()[0] ?? '');
  protected readonly id = computed(() => this.path()[1] ?? '');
  protected readonly kind = computed(() => {
    const [slug, second, third] = this.path();
    if (!slug) return 'index';
    if (!second) return 'list';
    if (second === 'create') return 'create';
    if (third === 'edit') return 'edit';
    if (third === 'delete') return 'delete';
    return 'details';
  });
}
