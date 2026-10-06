import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';

@Component({
  selector: 'app-flip-digit',
  standalone: false,
  templateUrl: './flip-digit.html',
  styleUrl: './flip-digit.css'
})
export class FlipDigit implements OnChanges {
  @Input() value = '0';

  currentValue = '0';
  nextValue = '0';
  isFlipping = false;

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['value']) {
      return;
    }

    const next = String(this.value ?? '0').slice(-1);

    if (changes['value'].firstChange) {
      this.currentValue = next;
      this.nextValue = next;
      return;
    }

    if (this.currentValue === next || this.isFlipping) {
      if (!this.isFlipping) {
        this.currentValue = next;
      }
      return;
    }

    this.nextValue = next;
    this.isFlipping = true;
  }

  onAnimationEnd(): void {
    this.currentValue = this.nextValue;
    this.isFlipping = false;
  }
}
