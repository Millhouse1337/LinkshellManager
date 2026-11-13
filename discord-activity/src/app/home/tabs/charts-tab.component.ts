import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, model, signal } from '@angular/core';

import { ChartBoardService } from '../../discord/chart-board.service';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import {
  ChartBoardSectionComponent,
  chartViewTabs,
  type ChartViewTab
} from './chart-board-section.component';
import { CHARTS_SECTIONS, type ChartsSectionOption } from './charts-sections';

/**
 * Charts: the endgame boards. Sky, Sea, Dynamis and Limbus are all live.
 *
 * The sub-section is a two-way `view` binding, not a router child. ActivityHomeComponent keeps it
 * in the location with the tab (/charts/sea on the website, in memory inside Discord, where the
 * iframe's URL is never touched because a stateful one interacts badly with the Activity SDK's URL
 * mapping). The segmented control is the same shape ItemsSectionComponent uses for Stockpile/Sold.
 *
 * `ready` stays on the interface even though nothing is false today: it is how the next unbuilt
 * board renders — disabled with a "Soon" badge rather than hidden, so the menu keeps its shape.
 *
 * HNM is deliberately absent even though the old placeholder listed it — it has its own first-class
 * tab and its own EnableHnmSection toggle, and the old website's Charts nav never listed it either.
 */
@Component({
  selector: 'app-charts-tab',
  imports: [CommonModule, ChartBoardSectionComponent],
  templateUrl: './charts-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ChartsTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  private readonly charts = inject(ChartBoardService);

  protected readonly sections = CHARTS_SECTIONS;

  readonly section = model<string>(CHARTS_SECTIONS[0].key, { alias: 'view' });

  /** The board's tabs (see chartViewTabs); empty for a board that keeps one page. */
  protected readonly viewTabs = computed(() => chartViewTabs(this.charts.board(this.section())));

  /**
   * The selected tab's key. Held HERE rather than in the board section so the switch can sit in
   * this page's header, at the top right. Kept across a board change, so Sky Holdings -> Sea lands
   * on Sea Holdings; a key the new board does not have falls back to its first tab.
   */
  protected readonly chartView = signal('items');

  protected readonly activeTab = computed<ChartViewTab | null>(() => {
    const tabs = this.viewTabs();
    return tabs.find(tab => tab.key === this.chartView()) ?? tabs[0] ?? null;
  });

  protected readonly linkshellId = computed<number | null>(() => this.activity.currentLinkshellId() || null);

  protected setSection(section: ChartsSectionOption): void {
    if (section.ready) {
      this.section.set(section.key);
    }
  }
}
