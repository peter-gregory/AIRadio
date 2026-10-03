import { Component, HostListener, OnDestroy, OnInit } from '@angular/core';
import {
  PlayingRadioState,
  Radio,
  RadioStation
} from '../shared/services/radio/radio';

interface CarouselStation extends RadioStation {
  isEmpty?: boolean;
}

@Component({
  selector: 'app-home',
  standalone: false,
  templateUrl: './home.html',
  styleUrl: './home.css',
})
export class Home implements OnInit, OnDestroy {
  private readonly emptyStation: CarouselStation = {
    id: '',
    name: 'No Station',
    isEmpty: true
  };

  private idleTimer?: ReturnType<typeof setTimeout>;
  private volumeTimer?: ReturnType<typeof setTimeout>;
  private touchStartX = 0;
  private touchStartY = 0;

  stations: CarouselStation[] = [];
  selectedIndex = 0;
  playing: PlayingRadioState = {
    playing: false,
    station: null
  };
  volume = 50;
  showVolume = false;
  showScreensaver = false;
  loading = true;

  constructor(private radioService: Radio) {
  }

  async ngOnInit(): Promise<void> {
    await this.refresh();
    this.resetIdleTimer();
  }

  ngOnDestroy(): void {
    this.clearTimer(this.idleTimer);
    this.clearTimer(this.volumeTimer);
  }

  async refresh(): Promise<void> {
    this.loading = true;

    try {
      const [stations, playing, volume] = await Promise.all([
        this.radioService.getStations(),
        this.radioService.getPlaying(),
        this.radioService.getVolume()
      ]);

      this.stations = [this.emptyStation, ...stations];

      this.playing = playing;
      this.volume = Math.max(0, Math.min(100, volume));

      const playingId = playing.stationId ?? playing.station?.id ?? '';
      const index = this.stations.findIndex(station => station.id === playingId);

      this.selectedIndex = index >= 0 ? index : 0;
    } finally {
      this.loading = false;
    }
  }

  get today(): Date {
    return new Date();
  }

  get clockHour(): string {
    return new Intl.DateTimeFormat(undefined, {
      hour: 'numeric',
      hour12: true
    }).formatToParts(new Date()).find(part => part.type === 'hour')?.value ?? '';
  }

  get clockMinute(): string {
    return new Intl.DateTimeFormat(undefined, {
      minute: '2-digit',
      hour12: true
    }).formatToParts(new Date()).find(part => part.type === 'minute')?.value ?? '';
  }

  get clockPeriod(): string {
    return new Intl.DateTimeFormat(undefined, {
      hour: 'numeric',
      hour12: true
    }).formatToParts(new Date()).find(part => part.type === 'dayPeriod')?.value ?? '';
  }

  get activeStation(): CarouselStation {
    return this.stations[this.selectedIndex] ?? this.emptyStation;
  }

  get previousStation(): CarouselStation {
    if (this.stations.length <= 1) {
      return this.emptyStation;
    }

    return this.stations[this.wrapIndex(this.selectedIndex - 1)];
  }

  get nextStation(): CarouselStation {
    if (this.stations.length <= 1) {
      return this.emptyStation;
    }

    return this.stations[this.wrapIndex(this.selectedIndex + 1)];
  }

  async selectPrevious(): Promise<void> {
    if (this.stations.length <= 1) {
      return;
    }

    this.selectedIndex = this.wrapIndex(this.selectedIndex - 1);
    await this.selectActiveStation();
  }

  async selectNext(): Promise<void> {
    if (this.stations.length <= 1) {
      return;
    }

    this.selectedIndex = this.wrapIndex(this.selectedIndex + 1);
    await this.selectActiveStation();
  }

  async selectActiveStation(): Promise<void> {
    this.resetIdleTimer();

    const station = this.activeStation;

    if (station.isEmpty) {
      await this.radioService.playStation('');
      this.playing = {
        playing: false,
        station: null
      };
      return;
    }

    await this.radioService.playStation(station.id);

    this.playing = {
      ...this.playing,
      stationId: station.id,
      station,
      playing: true,
      title: null,
      artist: null,
      album: null
    };
  }

  async volumeUp(): Promise<void> {
    await this.changeVolume(1);
  }

  async volumeDown(): Promise<void> {
    await this.changeVolume(-1);
  }

  async changeVolume(delta: number): Promise<void> {
    this.resetIdleTimer();

    const nextVolume = Math.max(0, Math.min(100, this.volume + delta));
    if (nextVolume === this.volume) {
      this.showVolumeOverlay();
      return;
    }

    this.volume = nextVolume;
    this.showVolumeOverlay();

    await this.radioService.setVolume(this.volume);
  }

  showVolumeOverlay(): void {
    this.showVolume = true;
    this.clearTimer(this.volumeTimer);
    this.volumeTimer = setTimeout(() => {
      this.showVolume = false;
    }, 2500);
  }

  onTouchStart(event: TouchEvent): void {
    if (event.touches.length !== 1) {
      return;
    }

    this.touchStartX = event.touches[0].clientX;
    this.touchStartY = event.touches[0].clientY;
  }

  async onTouchEnd(event: TouchEvent): Promise<void> {
    if (event.changedTouches.length !== 1) {
      return;
    }

    this.resetIdleTimer();

    const endX = event.changedTouches[0].clientX;
    const endY = event.changedTouches[0].clientY;
    const deltaX = endX - this.touchStartX;
    const deltaY = endY - this.touchStartY;

    if (Math.abs(deltaX) < 30 || Math.abs(deltaX) < Math.abs(deltaY)) {
      return;
    }

    if (deltaX < 0) {
      await this.selectNext();
    } else {
      await this.selectPrevious();
    }
  }

  @HostListener('document:keydown', ['$event'])
  async onKeyDown(event: KeyboardEvent): Promise<void> {
    switch (event.key) {
      case 'ArrowLeft':
        event.preventDefault();
        await this.selectPrevious();
        break;

      case 'ArrowRight':
        event.preventDefault();
        await this.selectNext();
        break;

      case 'ArrowUp':
        event.preventDefault();
        await this.volumeUp();
        break;

      case 'ArrowDown':
        event.preventDefault();
        await this.volumeDown();
        break;

      case 'PageUp':
        event.preventDefault();
        await this.selectPrevious();
        break;

      case 'PageDown':
        event.preventDefault();
        await this.selectNext();
        break;
    }
  }

  wake(): void {
    this.resetIdleTimer();
  }

  private resetIdleTimer(): void {
    this.showScreensaver = false;
    this.clearTimer(this.idleTimer);
    this.idleTimer = setTimeout(() => {
      this.showScreensaver = true;
    }, 60000);
  }

  private wrapIndex(index: number): number {
    const count = this.stations.length;
    return ((index % count) + count) % count;
  }

  private clearTimer(timer?: ReturnType<typeof setTimeout>): void {
    if (timer) {
      clearTimeout(timer);
    }
  }
}
