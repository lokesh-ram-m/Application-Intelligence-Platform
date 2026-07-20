import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';

@Injectable({ providedIn: 'root' })
export class CustomerService {
  private readonly base = '/api/customer';

  constructor(private http: HttpClient) {}

  all() {
    return this.http.get('/api/customer');
  }

  find(id: number) {
    return this.http.get(`/api/customer/${id}`);
  }

  add(customer: any) {
    return this.http.post('/api/customer', customer);
  }
}
