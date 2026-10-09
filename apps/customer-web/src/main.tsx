import 'antd/dist/reset.css';
import './styles.css';

import { FoundationProvider } from '@monergy/ui-foundation';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

import { App } from './App';
import { ErrorBoundary } from './ErrorBoundary';

// Match the initial canvas before React mounts the public entry experience.
const midnightEntry =
  window.location.pathname === '/access' ||
  window.location.pathname.startsWith('/access/') ||
  window.location.pathname === '/operations' ||
  import.meta.env.VITE_MONERGY_LOCAL_UAT === 'true';
document.documentElement.dataset.monergyTheme = midnightEntry ? 'midnight' : 'light';
const rootElement = document.querySelector('#root');
if (!(rootElement instanceof HTMLElement)) {
  throw new Error('Application root was not found.');
}

createRoot(rootElement).render(
  <StrictMode>
    <FoundationProvider>
      <ErrorBoundary>
        <App />
      </ErrorBoundary>
    </FoundationProvider>
  </StrictMode>,
);
