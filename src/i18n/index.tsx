import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";

import en from "./catalogs/en.json";

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

type Catalog = Readonly<Record<string, string>>;
type CatalogLocale = LocaleTag | "qps-ploc";

// English ships in the main bundle, as the fallback every key resolves to.
// Each other catalog is its own chunk, fetched only when it is the one in use:
// thirteen of them were 1.9 MB of strings parsed on every start.
const LOADERS = import.meta.glob<Catalog>(
  [
    "./catalogs/*.json",
    "!./catalogs/en.json",
    "!./catalogs/baseline-keys.json",
    "!./catalogs/catalog-meta.json",
    "!./catalogs/rewrite-keys.json",
  ],
  { import: "default" },
);

const CATALOGS = new Map<CatalogLocale, Catalog>([["en", en]]);

/** The catalog for a locale, from its chunk the first time. */
export async function loadCatalog(locale: CatalogLocale): Promise<Catalog> {
  const cached = CATALOGS.get(locale);
  if (cached !== undefined) return cached;
  const load = LOADERS[`./catalogs/${locale}.json`];
  if (load === undefined) return en;
  const catalog = await load();
  CATALOGS.set(locale, catalog);
  return catalog;
}

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
  const lang = locale === "qps-ploc" ? "en-XA" : locale;
  // A new language re-renders every control at once, and each one started
  // its hover transitions; WebKit left those unstarted while the window was
  // covered, so a still window looked busy. The switch lands without them.
  if (root.lang !== "" && root.lang !== lang) {
    root.dataset["settling"] = "true";
    requestAnimationFrame(() => requestAnimationFrame(() => delete root.dataset["settling"]));
  }
  root.lang = lang;
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

/** `t` outside React, for a locale whose catalog it loads first. */
export async function translatorFor(
  locale: LocaleTag,
): Promise<(key: MessageKey, values?: readonly unknown[]) => string> {
  const catalog = await loadCatalog(locale);
  return (key, values = []) => formatMessage(catalog[key] ?? en[key] ?? String(key), values, locale);
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
  /**
   * A language the host decides. Given, it wins over the preference and the
   * system, and the page's own `lang` and `dir` are left to the host.
   */
  readonly locale?: LocaleTag | undefined;
}

function currentSystemLocales(): readonly string[] {
  return typeof navigator === "undefined" ? ["en"] : [...navigator.languages];
}

export function I18nProvider({ children, initialPreference = "system", locale: pinned }: I18nProviderProps) {
  const [preference, setPreference] = useState<LocalePreference>(initialPreference);
  const [systemLocales, setSystemLocales] = useState<readonly string[]>(currentSystemLocales);
  const resolved = useMemo(
    () => resolveLocale(preference, systemLocales),
    [preference, systemLocales],
  );
  const locale = pinned ?? resolved;
  const hosted = pinned !== undefined;

  // Bumped when a catalog chunk lands, so the strings re-resolve.
  const [, setLoaded] = useState(0);
  const catalog = CATALOGS.get(locale);

  useEffect(() => {
    if (!hosted) applyDocumentLocale(locale);
    if (CATALOGS.has(locale)) return undefined;
    let current = true;
    void loadCatalog(locale).then(() => {
      if (current) setLoaded((count) => count + 1);
    });
    return () => {
      current = false;
    };
  }, [hosted, locale]);

  useEffect(() => {
    if (typeof window === "undefined") return;
    const changed = () => setSystemLocales(currentSystemLocales());
    window.addEventListener("languagechange", changed);
    return () => window.removeEventListener("languagechange", changed);
  }, []);

  const t = useCallback(
    (key: MessageKey, values: readonly unknown[] = []) => {
      const template = catalog?.[key] ?? en[key] ?? String(key);
      return formatMessage(template, values, locale === "qps-ploc" ? "en" : locale);
    },
    [catalog, locale],
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
