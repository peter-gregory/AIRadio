import { HttpClientModule } from '@angular/common/http';
import { NgModule, provideBrowserGlobalErrorListeners } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';

import { AppRoutingModule } from './app-routing-module';
import { App } from './app';
import { FormsModule } from '@angular/forms';
import { NgSelectModule } from '@ng-select/ng-select';
import { Home } from './home/home';
import { FlipDigit } from './shared/components/flip-digit/flip-digit';

@NgModule({
  declarations: [
    App,
    Home,
    FlipDigit
  ],
  imports: [
    BrowserModule, HttpClientModule, FormsModule, NgSelectModule,
    AppRoutingModule
  ],
  providers: [
    provideBrowserGlobalErrorListeners()
  ],
  bootstrap: [App]
})
export class AppModule { }
