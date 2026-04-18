import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./home/activity-home.component').then((module) => module.ActivityHomeComponent)
  },
  {
    path: '**',
    redirectTo: ''
  }
];
