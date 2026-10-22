import { Routes, UrlMatcher } from '@angular/router';

// Every path under the app's base href is the home component, which reads its tab (and any
// sub-view) from the URL itself (home/tab-routes.ts). One route for every path means switching
// tabs reuses the component instead of tearing it down and re-creating it.
const activityMatcher: UrlMatcher = segments => ({ consumed: segments });

export const routes: Routes = [
  {
    matcher: activityMatcher,
    loadComponent: () =>
      import('./home/activity-home.component').then((module) => module.ActivityHomeComponent)
  }
];
