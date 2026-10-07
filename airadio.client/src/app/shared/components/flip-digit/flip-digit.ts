import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';

@Component({
  selector: 'app-flip-digit',
  standalone: false,
  templateUrl: './flip-digit.html',
  styleUrl: './flip-digit.css'
})
export class FlipDigit implements OnChanges {
  @Input() value = '0';
  @Input() period = '';

  currentValue = '0';
  nextValue = '0';
  isFlipping = false;
  isBottomFlipping = false;

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['value']) {
      return;
    }

    const next = String(this.value ?? '0');

    if (changes['value'].firstChange) {
      this.currentValue = next;
      this.nextValue = next;
      return;
    }

    if (this.currentValue === next || this.isFlipping) {
      return;
    }

    this.nextValue = next;
    this.isBottomFlipping = false;
    this.isFlipping = true;
  }

  onTopAnimationEnd(): void {
    this.isBottomFlipping = true;
  }

  onBottomAnimationEnd(): void {
    this.currentValue = this.nextValue;
    this.isBottomFlipping = false;
    this.isFlipping = false;
  }
}