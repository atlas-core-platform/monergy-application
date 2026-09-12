import rawTokens from './tokens.json';

export interface SemanticTokens {
  color: {
    brand: { primary: string; primaryHover: string; onPrimary: string };
    neutral: Record<'0' | '50' | '100' | '300' | '600' | '800' | '950', string>;
    state: { success: string; warning: string; error: string; info: string };
  };
  typography: {
    fontFamily: string;
    fontFamilyMono: string;
    baseSize: string;
    lineHeight: number;
  };
  spacing: { unit: string; xs: string; sm: string; md: string; lg: string; xl: string };
  radius: { control: number; panel: number };
  elevation: { panel: string; dialog: string };
  focus: { color: string; width: string; offset: string };
  breakpoint: { sm: string; md: string; lg: string; xl: string };
}

export const semanticTokens: SemanticTokens = Object.freeze(rawTokens);
