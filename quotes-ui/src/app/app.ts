import { Component } from '@angular/core';
import { QuoteList } from './quote-list/quote-list';

@Component({
  selector: 'app-root',
  imports: [QuoteList],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {}
