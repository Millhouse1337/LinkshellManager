import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import type {
  ActivityDkpPoolEventType,
  ActivityDkpPoolInput,
  ActivityDkpPoolPreview
} from '../../discord/discord-activity.types';

// One editable DKP pool row. id is null for a pool the officer just added.
interface PoolDraft {
  id: number | null;
  name: string;
  accent: string;
  isDefault: boolean;
}

// DKP grouping: which event types' DKP earns and spends together.
//
// A pool is a wallet. Each event type earns into exactly one pool, and loot from that event type
// is paid out of the same pool. The partition is enforced by the UI SHAPE: every event type has
// exactly ONE <select>, so it cannot end up in two pools no matter what the officer does.
//
// Lifted out of the Configurations tab, where it sat among Discord channels and permissions. It
// is a question about the DKP economy, so it belongs beside the ledger and the sheet that show
// that economy's results — the officer deciding how to split DKP wants those in the same view,
// not a tab away.
@Component({
  selector: 'app-dkp-grouping-tab',
  imports: [CommonModule, FormsModule],
  templateUrl: './dkp-grouping-tab.component.html',
  styleUrl: './dkp-grouping-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DkpGroupingTabComponent {
  protected readonly activity = inject(DiscordActivityService);

  // Assignments are keyed by pool INDEX, not id — a pool the officer just added has no id yet,
  // and they need to be able to create it and move event types into it in one save.
  protected poolDrafts: PoolDraft[] = [];
  protected poolByEventType: Record<string, number> = {};
  protected readonly poolEventTypes = signal<ActivityDkpPoolEventType[]>([]);
  protected readonly poolAccents = signal<string[]>([]);
  protected readonly poolPreview = signal<ActivityDkpPoolPreview | null>(null);

  // Which linkshell was loaded, so the effect below reloads on a switch and not on every read.
  private loadedForLinkshellId: number | null = null;

  constructor() {
    effect(() => {
      const id = this.targetLinkshellId();
      if (!id || id === this.loadedForLinkshellId) { return; }
      this.loadedForLinkshellId = id;
      queueMicrotask(() => void this.loadDkpPools());
    });
  }

  // The linkshell being configured: the one selected on the dashboard, same source the
  // Configurations tab used when this lived there.
  protected targetLinkshellId(): number {
    const overview = this.activity.overview();
    return overview?.primaryLinkshell?.id ?? overview?.appUser?.primaryLinkshellId ?? 0;
  }

  protected activeLinkshellName = computed(() => {
    const id = this.targetLinkshellId();
    return (this.activity.overview()?.linkshells ?? []).find(l => l.id === id)?.name ?? null;
  });

  // Same permission that gated the card in Configurations. Without it the section renders a
  // notice rather than an editor nobody's save would be accepted from.
  protected canCustomize(): boolean {
    const id = this.targetLinkshellId();
    const link = (this.activity.overview()?.linkshells ?? []).find(l => l.id === id);
    return !!link?.permissions?.canCustomizeLinkshell;
  }

  // Group-colour name → theme token. Keys mirror the server's DkpPoolAccents; unknown / legacy
  // keys fall back to blue. Mirrors SwatchColor() in the web Customize.cshtml.
  private static readonly POOL_SWATCH: Record<string, string> = {
    blue: 'var(--accent)', green: 'var(--success)', red: 'var(--danger)',
    orange: 'var(--orange)', gold: 'var(--gold)', purple: 'var(--purple)', cyan: 'var(--cyan)',
    gray: 'var(--fg-3)',
  };

  protected poolSwatchColor(accent: string | null | undefined): string {
    return DkpGroupingTabComponent.POOL_SWATCH[(accent ?? '').toLowerCase()] ?? 'var(--accent)';
  }

  protected async loadDkpPools(): Promise<void> {
    const id = this.targetLinkshellId();
    if (!id || !this.canCustomize()) {
      this.poolDrafts = [];
      this.poolByEventType = {};
      this.poolEventTypes.set([]);
      this.poolAccents.set([]);
      this.poolPreview.set(null);
      return;
    }
    const data = await this.activity.loadDkpPools(id);
    if (!data) { return; }

    this.poolDrafts = data.pools.map(pool => ({
      id: pool.id,
      name: pool.name,
      accent: pool.accent,
      isDefault: pool.isDefault
    }));
    this.poolEventTypes.set(data.assignableEventTypes);
    this.poolAccents.set(data.accents);
    this.poolPreview.set(null);

    // -1 means "leave it on Default": an event type nothing has claimed is NOT pinned to the
    // default pool — otherwise saving would silently materialize a mapping row for every event
    // type in the catalog, and a later change to the default would stop reaching them.
    const assignments: Record<string, number> = {};
    const defaultIndex = data.pools.findIndex(pool => pool.isDefault);
    for (const type of data.assignableEventTypes) {
      const idx = data.pools.findIndex(
        pool => pool.eventTypes.some(t => t.toLowerCase() === type.key.toLowerCase()));
      assignments[type.key] = idx < 0 || idx === defaultIndex ? -1 : idx;
    }
    this.poolByEventType = assignments;
  }

  protected addPool(): void {
    this.poolDrafts = [
      ...this.poolDrafts,
      { id: null, name: '', accent: this.poolAccents()[0] ?? 'Blue', isDefault: false }
    ];
    this.poolPreview.set(null);
  }

  protected removePool(index: number): void {
    // The default group is permanent — it is where every unassigned event type, plus
    // adjustments and imports, land.
    if (this.poolDrafts[index]?.isDefault) { return; }
    this.poolDrafts = this.poolDrafts.filter((_, i) => i !== index);

    // Indices SHIFT when a pool is removed. Event types pointing at the removed pool become
    // unassigned; those above it slide down one. Without the remap they would point at whatever
    // pool happens to now occupy that index — a wrong answer that looks right.
    const remapped: Record<string, number> = {};
    for (const [type, poolIndex] of Object.entries(this.poolByEventType)) {
      remapped[type] = poolIndex === index ? -1 : poolIndex > index ? poolIndex - 1 : poolIndex;
    }
    this.poolByEventType = remapped;
    this.poolPreview.set(null);
  }

  protected setPoolForEventType(eventType: string, value: number): void {
    this.poolByEventType = { ...this.poolByEventType, [eventType]: value };
    this.poolPreview.set(null);
  }

  // Any edit invalidates a preview computed from the previous shape.
  protected markPoolsDirty(): void {
    this.poolPreview.set(null);
  }

  private buildPoolInputs(): ActivityDkpPoolInput[] {
    return this.poolDrafts.map((pool, index) => ({
      id: pool.id,
      name: pool.name.trim(),
      isDefault: pool.isDefault,
      accent: pool.accent,
      eventTypes: Object.entries(this.poolByEventType)
        .filter(([, poolIndex]) => poolIndex === index)
        .map(([type]) => type)
    }));
  }

  // Mirrors the server's validation so a bad save is caught before the round-trip.
  protected dkpPoolsError(): string | null {
    const named = this.poolDrafts.filter(pool => pool.name.trim().length > 0);
    if (named.length === 0) { return 'Keep at least one DKP pool.'; }
    const names = named.map(pool => pool.name.trim().toLowerCase());
    if (new Set(names).size !== names.length) {
      return 'Two pools have the same name — give them different names.';
    }
    return null;
  }

  protected async previewDkpPools(): Promise<void> {
    const id = this.targetLinkshellId();
    if (!id || this.dkpPoolsError()) { return; }
    this.poolPreview.set(await this.activity.previewDkpPools(id, this.buildPoolInputs()));
  }

  protected async saveDkpPools(): Promise<void> {
    const id = this.targetLinkshellId();
    if (!id || this.dkpPoolsError()) { return; }
    if (await this.activity.saveDkpPools(id, this.buildPoolInputs())) {
      await this.loadDkpPools();
    }
  }
}
