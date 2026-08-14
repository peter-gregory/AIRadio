import { Component } from '@angular/core';
import { TurnData, Radio } from '../shared/services/radio/radio';
import { NgForm } from '@angular/forms';

@Component({
  selector: 'app-home',
  standalone: false,
  templateUrl: './home.html',
  styleUrl: './home.css',
})
export class Home {

  turns: TurnData[] = [];
  prompt: string = "";

  constructor(private radioService: Radio) { }

  async executeTurn(form: NgForm) {
    var array = this.turns.slice();
    array.push({ prompt: this.prompt });
    this.turns = array.slice();
    this.prompt = ""
    form.reset();
    var reply = await this.radioService.executeTurn(array[array.length - 1].prompt!);
    array[array.length - 1].response = reply.response;
    this.turns = array.slice();
  }

}
