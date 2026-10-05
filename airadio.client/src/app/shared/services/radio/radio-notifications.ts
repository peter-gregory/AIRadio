import { Injectable, NgZone } from '@angular/core';
import { Subject } from 'rxjs';

export type RadioNotification =
  | {
      type: 'stationPlaylistChanged';
    }
  | {
      type: 'selectedStationChanged' | 'stationPlaybackStarted' | 'stationPlaybackStopped';
      stationId?: string | null;
    }
  | {
      type: 'songMetadataChanged';
      stationId?: string | null;
      title?: string | null;
      artist?: string | null;
      album?: string | null;
    }
  | {
      type: 'volumeChanged';
      volume: number;
    };

@Injectable({
  providedIn: 'root',
})
export class RadioNotifications {
  private readonly notificationsSubject = new Subject<RadioNotification>();
  readonly notifications$ = this.notificationsSubject.asObservable();

  private socket?: WebSocket;
  private reconnectTimer?: ReturnType<typeof setTimeout>;
  private stopped = false;

  constructor(private zone: NgZone) {
  }

  connect(): void {
    if (this.stopped ||
        this.socket?.readyState === WebSocket.OPEN ||
        this.socket?.readyState === WebSocket.CONNECTING) {
      return;
    }

    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socketUrl = `${protocol}//${window.location.host}/ws/radio`;

    this.socket = new WebSocket(socketUrl);

    this.socket.onmessage = event => {
      try {
        const notification = JSON.parse(event.data) as RadioNotification;

        this.zone.run(() => {
          this.notificationsSubject.next(notification);
        });
      } catch (error) {
        console.warn('Invalid radio WebSocket notification.', error);
      }
    };

    this.socket.onclose = () => {
      this.socket = undefined;

      if (!this.stopped) {
        this.reconnectTimer = setTimeout(() => {
          this.reconnectTimer = undefined;
          this.connect();
        }, 2000);
      }
    };

    this.socket.onerror = () => {
      this.socket?.close();
    };
  }

  disconnect(): void {
    this.stopped = true;

    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = undefined;
    }

    this.socket?.close();
    this.socket = undefined;
  }
}
