import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

// Pure shell now: the wordmark, a nav, and the routed view. All screen content
// lives behind lazy routes (app.routes.ts) - App itself imports no feature
// component, which is what keeps them out of the initial bundle.
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {}
