import type { ThemeConfig } from 'antd';

import { semanticTokens } from './tokens';

const remToPixels = (value: string) => Number.parseFloat(value) * 16;

export const monergyTheme: ThemeConfig = {
  cssVar: { prefix: 'monergy-ant' },
  hashed: true,
  token: {
    motion: false,
    colorPrimary: semanticTokens.color.brand.primary,
    colorPrimaryHover: semanticTokens.color.brand.primaryHover,
    colorSuccess: semanticTokens.color.state.success,
    colorWarning: semanticTokens.color.state.warning,
    colorError: semanticTokens.color.state.error,
    colorInfo: semanticTokens.color.state.info,
    colorText: semanticTokens.color.neutral['950'],
    colorTextSecondary: semanticTokens.color.neutral['600'],
    colorBgBase: semanticTokens.color.neutral['0'],
    colorBorder: semanticTokens.color.neutral['300'],
    fontFamily: semanticTokens.typography.fontFamily,
    fontSize: Number.parseInt(semanticTokens.typography.baseSize, 10),
    lineHeight: semanticTokens.typography.lineHeight,
    padding: remToPixels(semanticTokens.spacing.md),
    paddingSM: remToPixels(semanticTokens.spacing.sm),
    paddingLG: remToPixels(semanticTokens.spacing.lg),
    margin: remToPixels(semanticTokens.spacing.md),
    marginSM: remToPixels(semanticTokens.spacing.sm),
    marginLG: remToPixels(semanticTokens.spacing.lg),
    borderRadius: semanticTokens.radius.control,
    boxShadow: semanticTokens.elevation.panel,
    boxShadowSecondary: semanticTokens.elevation.dialog,
    controlOutline: semanticTokens.focus.color,
    screenSM: remToPixels(semanticTokens.breakpoint.sm),
    screenMD: remToPixels(semanticTokens.breakpoint.md),
    screenLG: remToPixels(semanticTokens.breakpoint.lg),
    screenXL: remToPixels(semanticTokens.breakpoint.xl),
  },
};
