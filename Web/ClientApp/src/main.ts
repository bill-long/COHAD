import { enableProdMode } from '@angular/core';
import { platformBrowserDynamic } from '@angular/platform-browser-dynamic';

import { AppModule } from './app/app.module';
import { environment } from './environments/environment';
import { captureUnsubscribeCredentialFromUrl } from './app/services/unsubscribe-credential-capture';

if (environment.production) {
  enableProdMode();
}

// Before bootstrap, and before anything can read location.pathname - the unsubscribe short link
// carries a bearer credential in the path, and observers we do not control latch onto it at
// initialisation. See the module for why this is a rewrite rather than a redaction.
captureUnsubscribeCredentialFromUrl();

platformBrowserDynamic()
  .bootstrapModule(AppModule)
  .catch(err => console.error(err));
