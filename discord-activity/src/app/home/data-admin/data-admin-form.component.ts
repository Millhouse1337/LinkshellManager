import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';

import { formatActionError, formatError } from '../../discord/discord-activity.helpers';
import { AuthService } from '../../discord/auth.service';
import { DataAdminService } from '../../discord/data-admin.service';
import type { DataAdminField, DataAdminForm } from '../../discord/data-admin.types';

// Create (id null) or edit one row: one input per editable column, its kind chosen by the server
// (text, textarea, number, checkbox, datetime-local, or a select for a foreign key or a const
// column). A refused save comes back as the same form with each field's error.
@Component({
  selector: 'app-data-admin-form',
  templateUrl: './data-admin-form.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DataAdminFormComponent {
  readonly slug = input.required<string>();
  readonly id = input.required<string | null>();
  readonly navigate = output<string[]>();

  private readonly dataAdmin = inject(DataAdminService);
  private readonly auth = inject(AuthService);
  protected readonly form = signal<DataAdminForm | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  // Column name -> the value the form will send (a checkbox sends "true" / "false").
  protected values: Record<string, string> = {};

  constructor() {
    effect(() => {
      const slug = this.slug();
      const id = this.id();
      untracked(() => {
        this.form.set(null);
        this.error.set(null);
        this.dataAdmin.form(slug, id)
          .then(form => this.show(form))
          .catch(error => this.error.set(formatError(error)));
      });
    });
  }

  protected isChecked(field: DataAdminField): boolean {
    return this.values[field.name]?.toLowerCase() === 'true';
  }

  protected async save(): Promise<void> {
    if (this.saving()) return;
    this.saving.set(true);
    try {
      const result = await this.dataAdmin.save(this.slug(), this.id(), this.values);
      if ('saved' in result) {
        this.navigate.emit([this.slug(), result.saved.key]);
      } else {
        this.show(result.refused);
      }
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving failed.'));
    } finally {
      this.saving.set(false);
    }
  }

  protected cancel(): void {
    const id = this.id();
    this.navigate.emit(id === null ? [this.slug()] : [this.slug(), id]);
  }

  private show(form: DataAdminForm): void {
    this.values = Object.fromEntries(form.fields.map(field => [field.name, field.value ?? '']));
    // A select whose value isn't one of its options (a new row's "0" foreign key) displays its
    // first option, and that is what the Razor form posted. Send what is shown, not the hidden value.
    for (const field of form.fields) {
      if (field.inputType !== 'select') continue;
      const options = field.options ?? [];
      const value = this.values[field.name];
      const shown = options.some(option => option.value === value) || (!field.isRequired && value === '');
      if (!shown) {
        this.values[field.name] = field.isRequired ? (options[0]?.value ?? '') : '';
      }
    }
    this.form.set(form);
  }
}
