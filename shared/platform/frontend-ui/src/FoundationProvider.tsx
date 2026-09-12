import { ConfigProvider } from 'antd';
import type { PropsWithChildren } from 'react';

import { monergyTheme } from './theme';

export function FoundationProvider({ children }: PropsWithChildren) {
  return <ConfigProvider theme={monergyTheme}>{children}</ConfigProvider>;
}
