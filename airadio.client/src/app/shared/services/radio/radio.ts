import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { lastValueFrom } from 'rxjs';

@Injectable({
  providedIn: 'root',
})
export class Radio {
  constructor(private http: HttpClient) {
  }

  async executeTurn(prompt: string): Promise<TurnData> {
    return await lastValueFrom(this.http.post<TurnData>('/api/radio/turn', { prompt: prompt }));
  }
}


export interface TurnData {
  prompt?: string;
  response?: string;
}

