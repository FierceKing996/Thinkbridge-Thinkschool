import { Component } from '@angular/core';
import { QuoteList } from './quote-list/quote-list';
import { CreateQuote } from './create-quote/create-quote';

@Component({
  selector: 'app-root',
  imports: [QuoteList, CreateQuote],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {}
