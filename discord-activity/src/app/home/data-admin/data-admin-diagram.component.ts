import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  inject,
  input,
  output,
  viewChild
} from '@angular/core';

import type { DataAdminSchema } from '../../discord/data-admin.types';

interface SchemaDiagramApi {
  mount(root: HTMLElement, graph: unknown, host: { navigate(url: string): void }): { destroy(): void };
}

// Records links inside the diagram point here; a click on one opens that table's list in the app.
const RECORDS_PATH = '/discord-activity/data-admin/';

// The diagram is the same code the Razor page runs: the site's stylesheet and two scripts
// (wwwroot/css/data-admin-schema.css, wwwroot/js/data-admin-schema-layout.js then
// data-admin-schema.js), loaded once, on first use. 'self' scripts and styles are allowed by the CSP.
let diagramAssets: Promise<void> | null = null;

function loadDiagramAssets(): Promise<void> {
  diagramAssets ??= (async () => {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = '/css/data-admin-schema.css';
    document.head.appendChild(link);
    await loadScript('/js/data-admin-schema-layout.js');
    await loadScript('/js/data-admin-schema.js');
  })().catch(error => {
    diagramAssets = null;
    throw error;
  });
  return diagramAssets;
}

function loadScript(src: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = src;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(`Could not load ${src}.`));
    document.head.appendChild(script);
  });
}

// Data Admin's schema diagram: every table as a card, every foreign key as a line, pan, zoom,
// focus mode and the side panel, exactly as on the Razor /data-admin page. This component only
// supplies the frame (the same data-erd-* hooks Views/DataAdmin/Index.cshtml carries) and mounts
// the shared script into it; destroying the tab destroys the diagram, key handler included.
@Component({
  selector: 'app-data-admin-diagram',
  templateUrl: './data-admin-diagram.component.html',
  styleUrl: './data-admin-diagram.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DataAdminDiagramComponent {
  readonly schema = input.required<DataAdminSchema>();
  readonly navigate = output<string[]>();

  protected readonly recordsUrlTemplate = `${RECORDS_PATH}__slug__`;
  private readonly frame = viewChild.required<ElementRef<HTMLElement>>('frame');
  private diagram: { destroy(): void } | null = null;
  private destroyed = false;

  constructor() {
    afterNextRender(() => void this.mount());
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.diagram?.destroy();
    });
  }

  // A plain left click on a records link opens the table in the app instead of reloading the
  // page; a middle click or a modifier still opens it in a new tab, as a link should.
  protected onClick(event: MouseEvent): void {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }
    const href = (event.target as Element | null)?.closest('a[href]')?.getAttribute('href');
    const slug = this.slugOf(href);
    if (slug) {
      event.preventDefault();
      this.navigate.emit([slug]);
    }
  }

  private async mount(): Promise<void> {
    try {
      await loadDiagramAssets();
    } catch {
      this.showEmpty();
      return;
    }
    const api = (window as unknown as { LsmSchemaDiagram?: SchemaDiagramApi }).LsmSchemaDiagram;
    if (this.destroyed || !api) {
      return;
    }
    this.diagram = api.mount(this.frame().nativeElement, this.schema().diagram, {
      // The v key.
      navigate: url => {
        const slug = this.slugOf(url);
        if (slug) this.navigate.emit([slug]);
      }
    });
  }

  private slugOf(url: string | null | undefined): string | null {
    if (!url?.startsWith(RECORDS_PATH)) return null;
    const slug = url.slice(RECORDS_PATH.length).split(/[/?#]/)[0];
    return slug ? decodeURIComponent(slug) : null;
  }

  private showEmpty(): void {
    const empty = this.frame().nativeElement.querySelector<HTMLElement>('[data-erd-empty]');
    if (empty) empty.hidden = false;
  }
}
