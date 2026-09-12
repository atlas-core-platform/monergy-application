import 'antd/dist/reset.css';
import './styles.css';

import { FoundationProvider } from '@monergy/ui-foundation';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

import { App } from './App';
import { ErrorBoundary } from './ErrorBoundary';

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
