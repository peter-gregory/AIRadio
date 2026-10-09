import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';

@Component({
  selector: 'app-flip-digit',
  standalone: false,
  templateUrl: './flip-digit.html',
  styleUrl: './flip-digit.css'
})
export class FlipDigit implements OnChanges {
  @Input() value = '0';
  @Input() topLabel = '';
  @Input() topRightLabel = '';
  @Input() period = '';

  currentValue = '0';
  nextValue = '0';
  currentTopLabel = '';
  nextTopLabel = '';
  currentTopRightLabel = '';
  nextTopRightLabel = '';
  currentPeriod = '';
  nextPeriod = '';
  isFlipping = false;
  isBottomFlipping = false;

  ngOnChanges(changes: SimpleChanges): void {
    const nextValue = String(this.value ?? '0');
    const nextTopLabel = String(this.topLabel ?? '');
    const nextTopRightLabel = String(this.topRightLabel ?? '');
    const nextPeriod = String(this.period ?? '');

    if (changes['value']?.firstChange) {
      this.currentValue = this.nextValue = nextValue;
      this.currentTopLabel = this.nextTopLabel = nextTopLabel;
      this.currentTopRightLabel = this.nextTopRightLabel = nextTopRightLabel;
      this.currentPeriod = this.nextPeriod = nextPeriod;
      return;
    }

    const changed = this.currentValue !== nextValue
      || this.currentTopLabel !== nextTopLabel
      || this.currentTopRightLabel !== nextTopRightLabel
      || this.currentPeriod !== nextPeriod;

    if (!changed || this.isFlipping) {
      return;
    }

    this.nextValue = nextValue;
    this.nextTopLabel = nextTopLabel;
    this.nextTopRightLabel = nextTopRightLabel;
    this.nextPeriod = nextPeriod;
    this.isBottomFlipping = false;
    this.isFlipping = true;
  }

  onTopAnimationEnd(): void {
    this.isBottomFlipping = true;
  }

  onBottomAnimationEnd(): void {
    this.currentValue = this.nextValue;
    this.currentTopLabel = this.nextTopLabel;
    this.currentTopRightLabel = this.nextTopRightLabel;
    this.currentPeriod = this.nextPeriod;
    this.isBottomFlipping = false;
    this.isFlipping = false;
  }
}