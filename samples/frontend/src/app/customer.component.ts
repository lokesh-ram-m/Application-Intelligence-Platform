import { Component } from '@angular/core';
import { CustomerService } from './customer.service';

@Component({
  selector: 'app-customer',
  template: '<ul><li *ngFor="let c of customers">{{ c.name }}</li></ul>'
})
export class CustomerComponent {
  customers: any[] = [];

  constructor(private service: CustomerService) {}

  load() {
    this.service.all().subscribe(list => (this.customers = list as any[]));
  }
}
