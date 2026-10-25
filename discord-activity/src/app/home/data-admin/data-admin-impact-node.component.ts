import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';

import type { DataAdminImpactNode } from '../../discord/data-admin.types';

// One branch of the delete preview's tree: which rows of a dependent table are deleted, unlinked,
// or block the delete, and below them whatever those rows take with them. Renders itself for the
// children, recursively.
@Component({
  selector: 'app-data-admin-impact-node',
  imports: [DecimalPipe],
  template: `
    <li style="margin:4px 0">
      <span [class]="'tag ' + tagClass()">{{ node().effect }}</span>
      <strong>{{ node().count | number }}</strong> {{ node().displayName }}
      <span class="muted">via {{ node().foreignKeyDisplayName }}</span>
      @if (node().alreadyCounted > 0) {
        <span class="muted" style="margin-left:4px">({{ node().alreadyCounted | number }} already counted above)</span>
      }
      @if (node().overflow || node().depthCapped) {
        <span class="muted" style="margin-left:4px">— not fully expanded</span>
      }
      @if (node().children.length > 0) {
        <ul style="margin:0;padding-left:18px">
          @for (child of node().children; track $index) {
            <app-data-admin-impact-node [node]="child" />
          }
        </ul>
      }
    </li>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DataAdminImpactNodeComponent {
  readonly node = input.required<DataAdminImpactNode>();

  protected tagClass(): string {
    const effect = this.node().effect;
    return effect === 'deleted' ? 'danger' : effect === 'blocked' ? 'warning' : 'default';
  }
}
