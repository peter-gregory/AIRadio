import { Component, OnInit, signal } from '@angular/core';
import { Radio, TurnData } from './shared/services/radio/radio';

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  standalone: false,
  styleUrl: './app.css'
})
export class App {

  protected readonly title = signal('AI Radio');
}
