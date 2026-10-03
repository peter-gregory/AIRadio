import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { lastValueFrom } from 'rxjs';

const RADIO_API = '/api/radio';

@Injectable({
  providedIn: 'root',
})
export class Radio {
  constructor(private http: HttpClient) {
  }

  async executeTurn(prompt: string): Promise<TurnData> {
    return await lastValueFrom(this.http.post<TurnData>(`${RADIO_API}/turn`, { prompt }));
  }

  async getStations(): Promise<RadioStation[]> {
    return await lastValueFrom(
      this.http.get<RadioStation[]>(`${RADIO_API}/stations`)
    );
  }

  async getPlaying(): Promise<PlayingRadioState> {
    return await lastValueFrom(
      this.http.get<PlayingRadioState>(`${RADIO_API}/playing`)
    );
  }

  async playStation(stationId: string): Promise<void> {
    await lastValueFrom(
      this.http.post<void>(`${RADIO_API}/play`, { stationId })
    );
  }

  async setVolume(volume: number): Promise<void> {
    const value = Math.max(0, Math.min(100, Math.round(volume)));

    await lastValueFrom(
      this.http.post<void>(`${RADIO_API}/volume`, { volume: value })
    );
  }

  async getVolume(): Promise<number> {
    return await lastValueFrom(
      this.http.get<number>(`${RADIO_API}/volume`)
    );
  }
}

export interface TurnData {
  prompt?: string;
  response?: string;
}

export interface RadioStation {
  id: string;
  name: string;
  streamUrl?: string;
  favicon?: string;
  description?: string;
  homepage?: string;
  isFavorite?: boolean;
  favoriteNumber?: number;
  country?: string;
  state?: string;
  tags?: string[];
  languages?: string[];
}

export interface PlayingRadioState {
  stationId?: string | null;
  station?: RadioStation | null;
  playing: boolean;
  title?: string | null;
  artist?: string | null;
  album?: string | null;
}
