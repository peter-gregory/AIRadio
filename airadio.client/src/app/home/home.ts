import { Component, HostListener, OnDestroy, OnInit } from '@angular/core';
import {
  PlayingRadioState,
  Radio,
  RadioStation
} from '../shared/services/radio/radio';
import {
  RadioNotification,
  RadioNotifications
} from '../shared/services/radio/radio-notifications';
import { Subscription } from 'rxjs';

interface CarouselStation extends RadioStation {
  isEmpty?: boolean;
}

type CarouselPosition =
  | 'far-previous'
  | 'previous'
  | 'active'
  | 'next'
  | 'far-next'
  | 'hidden';

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
  private notificationSubscription?: Subscription;
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

  constructor(
    private radioService: Radio,
    private radioNotifications: RadioNotifications
  ) {
  }

  async ngOnInit(): Promise<void> {
    this.notificationSubscription = this.radioNotifications.notifications$.subscribe(
      notification => this.handleNotification(notification)
    );

    await this.refresh();

    this.radioNotifications.connect();
    this.resetIdleTimer();
  }

  ngOnDestroy(): void {
    this.clearTimer(this.idleTimer);
    this.clearTimer(this.volumeTimer);
    this.notificationSubscription?.unsubscribe();
    this.radioNotifications.disconnect();
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

  private async handleNotification(notification: RadioNotification): Promise<void> {
    this.resetIdleTimer();

    switch (notification.type) {
      case 'stationPlaylistChanged':
        await this.refreshStations();
        break;

      case 'selectedStationChanged':
        this.selectStationById(notification.stationId);
        break;

      case 'stationPlaybackStarted':
        this.selectStationById(notification.stationId);
        this.updatePlayingState(notification.stationId, true);
        break;

      case 'stationPlaybackStopped':
        this.selectStationById(notification.stationId);
        this.updatePlayingState(notification.stationId, false);
        break;

      case 'songMetadataChanged':
        this.selectStationById(notification.stationId);
        this.playing = {
          ...this.playing,
          stationId: notification.stationId ?? this.playing.stationId ?? null,
          title: notification.title ?? null,
          artist: notification.artist ?? null,
          album: notification.album ?? null
        };
        break;

      case 'volumeChanged':
        this.volume = Math.max(0, Math.min(100, notification.volume));
        break;
    }
  }

  private async refreshStations(): Promise<void> {
    const stations = await this.radioService.getStations();
    const activeId = this.playing.stationId ?? this.activeStation.id;

    this.stations = [this.emptyStation, ...stations];

    const index = this.stations.findIndex(
      station => station.id === activeId
    );

    if (index >= 0) {
      this.selectedIndex = index;
    } else {
      this.selectedIndex = Math.min(
        this.selectedIndex,
        Math.max(0, this.stations.length - 1)
      );
    }
  }

  private selectStationById(stationId?: string | null): void {
    if (!stationId) {
      return;
    }

    const index = this.stations.findIndex(
      station => station.id === stationId
    );

    if (index >= 0) {
      this.selectedIndex = index;
    }
  }

  private updatePlayingState(
    stationId: string | null | undefined,
    isPlaying: boolean
  ): void {
    const station = stationId
      ? this.stations.find(item => item.id === stationId) ?? null
      : null;

    this.playing = {
      ...this.playing,
      stationId: stationId ?? this.playing.stationId ?? null,
      station: station ?? this.playing.station,
      playing: isPlaying
    };

    if (!isPlaying) {
      this.playing = {
        ...this.playing,
        title: null,
        artist: null,
        album: null
      };
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

  getCarouselPosition(index: number): CarouselPosition {
    const count = this.stations.length;

    if (count === 0) {
      return 'hidden';
    }

    let relative = index - this.selectedIndex;

    if (relative > count / 2) {
      relative -= count;
    } else if (relative <= -count / 2) {
      relative += count;
    }

    if (relative === -2) return 'far-previous';
    if (relative === -1) return 'previous';
    if (relative === 0) return 'active';
    if (relative === 1) return 'next';
    if (relative === 2) return 'far-next';

    return 'hidden';
  }

  async selectCard(index: number): Promise<void> {
    if (index < 0 || index >= this.stations.length) {
      return;
    }

    this.resetIdleTimer();
    this.selectedIndex = index;
    await this.selectActiveStation();
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
