import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { ChartBoardSectionComponent } from './chart-board-section.component';

/** One entry in the Charts sub-nav. Unbuilt boards render disabled rather than hidden. */
interface ChartsSectionOption {
  key: string;
  label: string;
  ready: boolean;
}

/**
 * Charts: the endgame boards. Sky, Sea, Dynamis and Limbus are all live.
 *
 * The sub-section is component state rather than a router child on purpose. app.routes.ts has a
 * single route and no tab is routed, so a child route here would be the only one, and making the
 * Discord iframe's URL stateful interacts badly with the Activity SDK's URL mapping. The segmented
 * control is the same shape ItemsSectionComponent uses for Stockpile/Sold.
 *
 * `ready` stays on the interface even though nothing is false today: it is how the next unbuilt
 * board renders — disabled with a "Soon" badge rather than hidden, so the menu keeps its shape.
 *
 * HNM is deliberately absent even though the old placeholder listed it — it has its own first-class
 * tab and its own EnableHnmSection toggle, and the website's Charts nav never listed it either.
 */
@Component({
  selector: 'app-charts-tab',
  imports: [CommonModule, ChartBoardSectionComponent],
  templateUrl: './charts-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ChartsTabComponent {
  protected readonly activity = inject(DiscordActivityService);

  // Mirrors the server's ChartBoardCatalog for the live boards. Kept as a literal rather than
  // fetched because the sub-nav has to render before any board loads; the server still validates
  // the board on every call, so a stale entry here is a 404, never a wrong board.
  protected readonly sections: ChartsSectionOption[] = [
    { key: 'Sky', label: 'Sky', ready: true },
    { key: 'Sea', label: 'Sea', ready: true },
    { key: 'Dynamis', label: 'Dynamis', ready: true },
    { key: 'Limbus', label: 'Limbus', ready: true },
    { key: 'HENM', label: 'HENM', ready: true }
  ];

  protected readonly section = signal<string>('Sky');

  protected readonly linkshellId = computed<number | null>(() =>
    this.activity.overview()?.appUser?.primaryLinkshellId
      ?? this.activity.overview()?.primaryLinkshell?.id
      ?? this.activity.overview()?.linkshells?.[0]?.id
      ?? null
  );

  protected setSection(section: ChartsSectionOption): void {
    if (section.ready) {
      this.section.set(section.key);
    }
  }
}
