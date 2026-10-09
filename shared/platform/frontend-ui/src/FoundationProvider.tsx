import { ConfigProvider } from 'antd';
import type { PropsWithChildren } from 'react';

import { midnightTheme, monergyTheme } from './theme';

export function FoundationProvider({
  children,
  appearance = 'light',
}: PropsWithChildren<{ appearance?: 'light' | 'midnight' }>) {
  return (
    <ConfigProvider theme={appearance === 'midnight' ? midnightTheme : monergyTheme}>
      {children}
    </ConfigProvider>
  );
}
