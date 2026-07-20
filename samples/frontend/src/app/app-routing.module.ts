import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { CustomerComponent } from './customer.component';

const routes: Routes = [
  { path: '', component: CustomerComponent },
  { path: 'customers', component: CustomerComponent },
  { path: 'customers/:id', component: CustomerComponent }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule {}
