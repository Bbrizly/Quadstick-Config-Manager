import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";

import ar from "./catalogs/ar.json";
import de from "./catalogs/de.json";
import en from "./catalogs/en.json";
import es from "./catalogs/es.json";
import fr from "./catalogs/fr.json";
import hi from "./catalogs/hi.json";
import it from "./catalogs/it.json";
import ja from "./catalogs/ja.json";
import ko from "./catalogs/ko.json";
import nl from "./catalogs/nl.json";
import pl from "./catalogs/pl.json";
import pt from "./catalogs/pt.json";
import pseudo from "./catalogs/qps-ploc.json";
import zhHans from "./catalogs/zh-Hans.json";

export const LOCALE_TAGS = [
  "en",
  "ar",
  "de",
  "es",
  "fr",
  "hi",
  "it",
  "ja",
  "ko",
  "nl",
  "pl",
  "pt",
  "zh-Hans",
] as const;

export type LocaleTag = (typeof LOCALE_TAGS)[number];
export type LocalePreference = "system" | LocaleTag | "qps-ploc";
export type MessageKey = keyof typeof en;
export type TextDirection = "ltr" | "rtl";

const CATALOGS: Record<LocaleTag | "qps-ploc", Record<string, string>> = {
  en,
  ar,
  de,
  es,
  fr,
  hi,
  it,
  ja,
  ko,
  nl,
  pl,
  pt,
  "zh-Hans": zhHans,
  "qps-ploc": pseudo,
};

export const LOCALE_NAMES: Readonly<Record<LocaleTag, string>> = {
  en: "English",
  ar: "العربية",
  de: "Deutsch",
  es: "Español",
  fr: "Français",
  hi: "हिन्दी",
  it: "Italiano",
  ja: "日本語",
  ko: "한국어",
  nl: "Nederlands",
  pl: "Polski",
  pt: "Português",
  "zh-Hans": "简体中文",
};

export function localeDirection(locale: LocaleTag | "qps-ploc"): TextDirection {
  return locale === "ar" ? "rtl" : "ltr";
}

export function resolveLocale(
  preference: LocalePreference,
  systemLocales: readonly string[] = typeof navigator === "undefined" ? ["en"] : navigator.languages,
): LocaleTag | "qps-ploc" {
  if (preference !== "system") return preference;
  for (const requested of systemLocales) {
    const normalized = requested.toLowerCase();
    if (normalized.startsWith("zh-hans") || normalized === "zh-cn" || normalized === "zh-sg") {
      return "zh-Hans";
    }
    const direct = LOCALE_TAGS.find((tag) => normalized === tag.toLowerCase());
    if (direct !== undefined) return direct;
    const language = normalized.split("-")[0];
    const base = LOCALE_TAGS.find((tag) => tag.toLowerCase() === language);
    if (base !== undefined) return base;
  }
  return "en";
}

export function applyDocumentLocale(
  locale: LocaleTag | "qps-ploc",
  root: HTMLElement = document.documentElement,
): void {
  root.lang = locale === "qps-ploc" ? "en-XA" : locale;
  root.dir = localeDirection(locale);
  root.dataset["locale"] = locale;
}

function formatValue(value: unknown, pattern: string | undefined, locale: string): string {
  if (pattern === undefined || typeof value !== "number") return String(value ?? "");
  if (/^P\d+$/u.test(pattern)) {
    const digits = Number(pattern.slice(1));
    return new Intl.NumberFormat(locale, {
      style: "percent",
      maximumFractionDigits: digits,
      minimumFractionDigits: digits,
    }).format(value);
  }
  if (/^N\d+$/u.test(pattern)) {
    const digits = Number(pattern.slice(1));
    return new Intl.NumberFormat(locale, {
      maximumFractionDigits: digits,
      minimumFractionDigits: digits,
    }).format(value);
  }
  return String(value);
}

export function formatMessage(
  template: string,
  values: readonly unknown[] = [],
  locale = "en",
): string {
  return template.replace(/\{(\d+)(?::([^}]+))?\}/gu, (_match, indexText: string, pattern?: string) => {
    const value = values[Number(indexText)];
    return formatValue(value, pattern, locale);
  });
}

export function pluralCategory(locale: LocaleTag | "qps-ploc", count: number): "one" | "other" {
  if (locale === "fr") return count === 0 || count === 1 ? "one" : "other";
  return count === 1 ? "one" : "other";
}

interface I18nValue {
  readonly locale: LocaleTag | "qps-ploc";
  readonly preference: LocalePreference;
  readonly direction: TextDirection;
  readonly setPreference: (preference: LocalePreference) => void;
  readonly t: (key: MessageKey, values?: readonly unknown[]) => string;
  readonly plural: (prefix: string, count: number, values?: readonly unknown[]) => string;
}

const I18nContext = createContext<I18nValue | null>(null);

export interface I18nProviderProps {
  readonly children: ReactNode;
  readonly initialPreference?: LocalePreference;
}

function currentSystemLocales(): readonly string[] {
  return typeof navigator === "undefined" ? ["en"] : [...navigator.languages];
}

export function I18nProvider({ children, initialPreference = "system" }: I18nProviderProps) {
  const [preference, setPreference] = useState<LocalePreference>(initialPreference);
  const [systemLocales, setSystemLocales] = useState<readonly string[]>(currentSystemLocales);
  const locale = useMemo(
    () => resolveLocale(preference, systemLocales),
    [preference, systemLocales],
  );

  useEffect(() => {
    applyDocumentLocale(locale);
  }, [locale]);

  useEffect(() => {
    if (typeof window === "undefined") return;
    const changed = () => setSystemLocales(currentSystemLocales());
    window.addEventListener("languagechange", changed);
    return () => window.removeEventListener("languagechange", changed);
  }, []);

  const t = useCallback(
    (key: MessageKey, values: readonly unknown[] = []) => {
      const template = CATALOGS[locale][key] ?? CATALOGS.en[key] ?? String(key);
      return formatMessage(template, values, locale === "qps-ploc" ? "en" : locale);
    },
    [locale],
  );

  const plural = useCallback(
    (prefix: string, count: number, values: readonly unknown[] = [count]) => {
      const key = `${prefix}_${pluralCategory(locale, count)}` as MessageKey;
      return t(key, values);
    },
    [locale, t],
  );

  const value = useMemo<I18nValue>(
    () => ({ locale, preference, direction: localeDirection(locale), setPreference, t, plural }),
    [locale, preference, t, plural],
  );
  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nValue {
  const value = useContext(I18nContext);
  if (value === null) throw new Error("useI18n must be used inside I18nProvider");
  return value;
}
