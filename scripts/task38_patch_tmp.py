from __future__ import annotations

from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8")


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one match, found {count}: {old[:80]!r}")
    write(path, text.replace(old, new, 1))


def replace_between(path: str, start: str, end: str, replacement: str) -> None:
    text = read(path)
    first = text.find(start)
    if first < 0:
        raise SystemExit(f"{path}: start marker missing")
    last = text.find(end, first)
    if last < 0:
        raise SystemExit(f"{path}: end marker missing")
    write(path, text[:first] + replacement + text[last:])


# qcm-config: expose already-ported whole-mode semantics through typed EditorOp.
replace_once(
    "crates/qcm-config/src/editor_op.rs",
    '''    AddMode {\n        name: String,\n    },\n    RenameMode {''',
    '''    AddMode {\n        name: String,\n    },\n    DuplicateMode {\n        sheet: usize,\n        name: String,\n    },\n    DeleteMode {\n        sheet: usize,\n    },\n    MoveMode {\n        sheet: usize,\n        delta: isize,\n    },\n    RenameMode {''',
)
replace_once(
    "crates/qcm-config/src/editor_op.rs",
    '''            Self::AddMode { .. } => "add_mode",\n            Self::RenameMode { .. } => "rename_mode",''',
    '''            Self::AddMode { .. } => "add_mode",\n            Self::DuplicateMode { .. } => "duplicate_mode",\n            Self::DeleteMode { .. } => "delete_mode",\n            Self::MoveMode { .. } => "move_mode",\n            Self::RenameMode { .. } => "rename_mode",''',
)
replace_once(
    "crates/qcm-config/src/editor_op.rs",
    '''            EditorOp::AddMode { name } => self.add_mode_sheet(name).is_some(),\n            EditorOp::RenameMode { sheet, name } => self.rename_mode(*sheet, name),''',
    '''            EditorOp::AddMode { name } => self.add_mode_sheet(name).is_some(),\n            EditorOp::DuplicateMode { sheet, name } => self.duplicate_mode(*sheet, name).is_some(),\n            EditorOp::DeleteMode { sheet } => self.delete_mode(*sheet),\n            EditorOp::MoveMode { sheet, delta } => self.move_mode(*sheet, *delta),\n            EditorOp::RenameMode { sheet, name } => self.rename_mode(*sheet, name),''',
)

# Native IPC: one read-only session refresh plus exhaustive bounds for new ops.
replace_once(
    "src-tauri/src/ipc.rs",
    '''        EditorOp::AddMode { name } => vec![name.as_str()],\n        EditorOp::RenameMode { name, .. } => vec![name.as_str()],\n        EditorOp::SetModeChannel { channel, .. } => vec![channel.as_str()],\n        EditorOp::AddRow { .. }\n        | EditorOp::DeleteRow { .. }\n        | EditorOp::MoveRow { .. }\n        | EditorOp::Normalize => Vec::new(),''',
    '''        EditorOp::AddMode { name } | EditorOp::DuplicateMode { name, .. } => vec![name.as_str()],\n        EditorOp::RenameMode { name, .. } => vec![name.as_str()],\n        EditorOp::SetModeChannel { channel, .. } => vec![channel.as_str()],\n        EditorOp::AddRow { .. }\n        | EditorOp::DeleteRow { .. }\n        | EditorOp::MoveRow { .. }\n        | EditorOp::DeleteMode { .. }\n        | EditorOp::MoveMode { .. }\n        | EditorOp::Normalize => Vec::new(),''',
)
replace_once(
    "src-tauri/src/ipc.rs",
    '''/// What undo, save and save-as all need: which profile, and the revision the\n/// window was looking at when the user asked.\n#[derive(Debug, Clone, Deserialize)]\n#[serde(rename_all = "camelCase")]\npub struct SessionRevisionRequest {''',
    '''/// Read-only refresh of one open canonical session.\n#[derive(Debug, Clone, Deserialize)]\n#[serde(rename_all = "camelCase")]\npub struct SessionRequest {\n    pub session_id: String,\n}\n\n/// What undo, save and save-as all need: which profile, and the revision the\n/// window was looking at when the user asked.\n#[derive(Debug, Clone, Deserialize)]\n#[serde(rename_all = "camelCase")]\npub struct SessionRevisionRequest {''',
)

replace_once(
    "src-tauri/src/shell.rs",
    '''    AppSnapshotDto, ApplyEditorOpsRequest, CapabilitiesDto, CloseOutcomeDto, CloseProfileRequest,\n    NewProfileRequest, SessionRevisionRequest, UpdateSettingsRequest, parse, session_id,''',
    '''    AppSnapshotDto, ApplyEditorOpsRequest, CapabilitiesDto, CloseOutcomeDto, CloseProfileRequest,\n    NewProfileRequest, SessionRequest, SessionRevisionRequest, UpdateSettingsRequest, parse, session_id,''',
)
replace_once(
    "src-tauri/src/shell.rs",
    '''    pub fn choose_and_open_profile(&self) -> Result<Option<EditorSnapshot>, QcmError> {\n        let Some(target) = self.picker.pick_open()? else {\n            return Ok(None);\n        };\n        self.sessions().open_local(target).map(Some)\n    }\n\n    pub fn apply_editor_ops''',
    '''    pub fn choose_and_open_profile(&self) -> Result<Option<EditorSnapshot>, QcmError> {\n        let Some(target) = self.picker.pick_open()? else {\n            return Ok(None);\n        };\n        self.sessions().open_local(target).map(Some)\n    }\n\n    pub fn get_profile_snapshot(&self, raw: Value) -> Result<EditorSnapshot, QcmError> {\n        let request: SessionRequest = parse(raw, "get_profile_snapshot request")?;\n        let session = session_id(&request.session_id)?;\n        let sessions = self.sessions();\n        Ok(EditorSnapshot::of(sessions.session(session)?))\n    }\n\n    pub fn apply_editor_ops''',
)
replace_once(
    "src-tauri/src/commands.rs",
    '''pub fn choose_and_open_profile(\n    state: State<'_, ShellState>,\n) -> Result<Option<EditorSnapshot>, Failure> {\n    redact(state.choose_and_open_profile())\n}\n\n#[tauri::command]\npub fn apply_editor_ops''',
    '''pub fn choose_and_open_profile(\n    state: State<'_, ShellState>,\n) -> Result<Option<EditorSnapshot>, Failure> {\n    redact(state.choose_and_open_profile())\n}\n\n#[tauri::command]\npub fn get_profile_snapshot(\n    state: State<'_, ShellState>,\n    request: Value,\n) -> Result<EditorSnapshot, Failure> {\n    redact(state.get_profile_snapshot(request))\n}\n\n#[tauri::command]\npub fn apply_editor_ops''',
)
replace_once(
    "src-tauri/src/lib.rs",
    '''        "choose_and_open_profile",\n        "apply_editor_ops",''',
    '''        "choose_and_open_profile",\n        "get_profile_snapshot",\n        "apply_editor_ops",''',
)
replace_once(
    "src-tauri/src/lib.rs",
    '''            commands::choose_and_open_profile,\n            commands::apply_editor_ops,''',
    '''            commands::choose_and_open_profile,\n            commands::get_profile_snapshot,\n            commands::apply_editor_ops,''',
)
replace_once("src-tauri/src/lib.rs", "assert_eq!(commands.len(), 24);", "assert_eq!(commands.len(), 25);")

# Frontend contract mirrors the typed core exactly.
replace_once(
    "src/platform/contracts.ts",
    '''  | { readonly op: "add_mode"; readonly name: string }\n  | { readonly op: "rename_mode"; readonly sheet: number; readonly name: string }''',
    '''  | { readonly op: "add_mode"; readonly name: string }\n  | { readonly op: "duplicate_mode"; readonly sheet: number; readonly name: string }\n  | { readonly op: "delete_mode"; readonly sheet: number }\n  | { readonly op: "move_mode"; readonly sheet: number; readonly delta: -1 | 1 }\n  | { readonly op: "rename_mode"; readonly sheet: number; readonly name: string }''',
)
replace_once(
    "src/platform/qcmClient.ts",
    '''  newProfile(name: string): Promise<EditorSnapshot>;\n  chooseAndOpenProfile(): Promise<EditorSnapshot | null>;\n  applyEditorOps(''',
    '''  newProfile(name: string): Promise<EditorSnapshot>;\n  chooseAndOpenProfile(): Promise<EditorSnapshot | null>;\n  /** Re-read canonical Rust state after a revision conflict or save. */\n  getProfileSnapshot(sessionId: string): Promise<EditorSnapshot>;\n  applyEditorOps(''',
)
replace_once(
    "src/platform/tauriQcmClient.ts",
    '''  chooseAndOpenProfile(): Promise<EditorSnapshot | null> {\n    return call<EditorSnapshot | null>("choose_and_open_profile");\n  }\n\n  applyEditorOps(''',
    '''  chooseAndOpenProfile(): Promise<EditorSnapshot | null> {\n    return call<EditorSnapshot | null>("choose_and_open_profile");\n  }\n\n  getProfileSnapshot(sessionId: string): Promise<EditorSnapshot> {\n    return call<EditorSnapshot>("get_profile_snapshot", { sessionId });\n  }\n\n  applyEditorOps(''',
)
replace_once(
    "src/platform/tauriQcmClient.ts",
    '''  "choose_and_open_profile",\n  "apply_editor_ops",''',
    '''  "choose_and_open_profile",\n  "get_profile_snapshot",\n  "apply_editor_ops",''',
)

# The browser mock remains a contract double, not a JS config engine. It only
# models enough row/sheet mechanics to exercise presentation behavior.
replace_once(
    "src/platform/mockQcmClient.ts",
    '''  chooseAndOpenProfile(): Promise<EditorSnapshot | null> {\n    this.#dialogsOpened += 1;\n    const answer = this.#openAnswers.shift() ?? { cancelled: false, name: "Racing.csv" };\n    if (answer.cancelled) return Promise.resolve(null);\n    const grid = clone(TEMPLATE);\n    const nameRow = grid[1];\n    if (nameRow !== undefined) nameRow[0] = answer.name;\n    return Promise.resolve(\n      this.#open({ kind: "local", name: answer.name }, answer.name, answer.name, grid),\n    );\n  }\n\n  applyEditorOps(''',
    '''  chooseAndOpenProfile(): Promise<EditorSnapshot | null> {\n    this.#dialogsOpened += 1;\n    const answer = this.#openAnswers.shift() ?? { cancelled: false, name: "Racing.csv" };\n    if (answer.cancelled) return Promise.resolve(null);\n    const grid = clone(TEMPLATE);\n    const nameRow = grid[1];\n    if (nameRow !== undefined) nameRow[0] = answer.name;\n    return Promise.resolve(\n      this.#open({ kind: "local", name: answer.name }, answer.name, answer.name, grid),\n    );\n  }\n\n  getProfileSnapshot(sessionId: string): Promise<EditorSnapshot> {\n    const session = this.#sessions.get(sessionId);\n    if (session === undefined) return Promise.reject(this.#unknownSession());\n    return Promise.resolve(snapshot(session));\n  }\n\n  applyEditorOps(''',
)

new_apply = r'''function modeRanges(grid: readonly (readonly string[])[]): readonly { readonly mode: Mode; readonly start: number; readonly end: number }[] {
  const modes = modesOf(grid);
  return modes.map((mode, index) => ({
    mode,
    start: mode.startRow - 1,
    end: (modes[index + 1]?.startRow ?? grid.length + 1) - 1,
  }));
}

function apply(grid: string[][], op: EditorOp): string[][] | null {
  const next = clone(grid);
  switch (op.op) {
    case "set_cell": {
      if (op.row === 0) return null;
      while (next.length < op.row) next.push([]);
      const row = next[op.row - 1];
      if (row === undefined) return null;
      while (row.length <= op.col) row.push("");
      row[op.col] = op.value;
      return next;
    }
    case "set_output": {
      const row = next[op.row - 1];
      if (row === undefined) return null;
      while (row.length <= 11) row.push("");
      row[0] = op.token;
      row[11] = op.action ?? "";
      return next;
    }
    case "add_row": {
      const mode = modesOf(next).find((candidate) => candidate.index === op.sheet);
      if (mode === undefined) return null;
      const insertAt = mode.startRow + 2 + mode.bindingCount;
      next.splice(insertAt, 0, ["", "normal", ""]);
      return next;
    }
    case "delete_row": {
      if (op.row === 0 || op.row > next.length) return null;
      next.splice(op.row - 1, 1);
      return next;
    }
    case "move_row": {
      if (op.from === op.to || op.from === 0 || op.to === 0 || op.from > next.length || op.to > next.length) return null;
      const moved = next.splice(op.from - 1, 1)[0];
      if (moved === undefined) return null;
      next.splice(op.to - 1, 0, moved);
      return next;
    }
    case "add_mode": {
      const first = modesOf(next)[0];
      next.push(
        ["Profile Name", "", op.name],
        [],
        ["PlayStation Outputs", "Function", first?.channel ?? ""],
      );
      return next;
    }
    case "duplicate_mode": {
      const range = modeRanges(next)[op.sheet];
      if (range === undefined || op.name.trim() === "") return null;
      const copied = next.slice(range.start, range.end).map((row) => [...row]);
      const header = copied[0];
      if (header === undefined) return null;
      while (header.length <= 2) header.push("");
      header[2] = op.name.trim();
      const nameRow = copied[1];
      if (nameRow !== undefined && nameRow.length > 0) nameRow[0] = "";
      next.push(...copied);
      return next;
    }
    case "delete_mode": {
      const ranges = modeRanges(next);
      const range = ranges[op.sheet];
      if (range === undefined || ranges.length <= 1) return null;
      const fileName = op.sheet === 0 ? next[range.start + 1]?.[0] ?? "" : "";
      next.splice(range.start, range.end - range.start);
      if (op.sheet === 0) {
        const first = modesOf(next)[0];
        if (first === undefined) return null;
        const slotIndex = first.startRow;
        const slot = next[slotIndex] ?? [];
        if (next[slotIndex] === undefined) next[slotIndex] = slot;
        while (slot.length === 0) slot.push("");
        slot[0] = fileName;
      }
      return next;
    }
    case "move_mode": {
      const ranges = modeRanges(next);
      const current = ranges[op.sheet];
      const other = ranges[op.sheet + op.delta];
      if (current === undefined || other === undefined) return null;
      const lo = current.start < other.start ? current : other;
      const hi = current.start < other.start ? other : current;
      const loBlock = next.slice(lo.start, lo.end).map((row) => [...row]);
      const midBlock = next.slice(lo.end, hi.start).map((row) => [...row]);
      const hiBlock = next.slice(hi.start, hi.end).map((row) => [...row]);
      if (lo.mode.index === 0) {
        const fileName = loBlock[1]?.[0] ?? "";
        if (loBlock[1] !== undefined && loBlock[1].length > 0) loBlock[1][0] = "";
        const incoming = hiBlock[1] ?? [];
        if (hiBlock[1] === undefined) hiBlock[1] = incoming;
        while (incoming.length === 0) incoming.push("");
        incoming[0] = fileName;
      }
      next.splice(lo.start, hi.end - lo.start, ...hiBlock, ...midBlock, ...loBlock);
      return next;
    }
    case "rename_mode": {
      const mode = modesOf(next)[op.sheet];
      if (mode === undefined || op.name.trim() === "") return null;
      const keyword = next[mode.startRow - 1];
      if (keyword === undefined) return null;
      while (keyword.length <= 2) keyword.push("");
      keyword[2] = op.name.trim();
      return next;
    }
    case "set_mode_channel": {
      const mode = modesOf(next)[op.sheet];
      if (mode === undefined) return null;
      const header = next[mode.startRow + 1];
      if (header === undefined) return null;
      while (header.length <= 2) header.push("");
      header[2] = op.channel;
      return next;
    }
    case "normalize":
      return next;
  }
}

'''
replace_between(
    "src/platform/mockQcmClient.ts",
    "function apply(grid: string[][], op: EditorOp): string[][] | null {",
    "function stripUndefined",
    new_apply,
)

# App composition: Home becomes the editor immediately once a profile is open.
write(
    "src/app/App.tsx",
    r'''import { useCallback, useEffect, useState } from "react";

import { AppShell, type ShellDestination } from "../components/primitives/AppShell";
import { Dialog } from "../components/primitives/Dialog";
import { LiveRegion } from "../components/primitives/LiveRegion";
import { ToastRegion } from "../components/primitives/ToastRegion";
import { EditorWorkspace } from "../features/editor/EditorWorkspace";
import {
  I18nProvider,
  LOCALE_NAMES,
  LOCALE_TAGS,
  useI18n,
  type LocalePreference,
  type MessageKey,
} from "../i18n";
import { localizedErrorMessage } from "../i18n/errors";
import { MockQcmClient, asQcmError, type EditorSnapshot, type QcmClient } from "../platform";
import { applyThemePreference, type ThemePreference } from "./theme";

const DESTINATION_COPY: Record<ShellDestination, { title: MessageKey; detail: MessageKey }> = {
  home: { title: "Rewrite_ProductName", detail: "Shell_ProfilesYouSaveWillShow" },
  device: { title: "Shell_OnYourQuadStick", detail: "Shell_ManageTheProfileFilesOn" },
  community: {
    title: "Community_CommunityProfiles",
    detail: "Community_GameProfilesOtherQuadStickPlayers",
  },
};

const DEFAULT_CLIENT = new MockQcmClient();

interface AppProps {
  readonly client?: QcmClient;
}

function LocalizedApp({ client }: { readonly client: QcmClient }) {
  const { t, preference, setPreference } = useI18n();
  const [activeDestination, setActiveDestination] = useState<ShellDestination>("home");
  const [themePreference, setThemePreference] = useState<ThemePreference>("system");
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [editor, setEditor] = useState<EditorSnapshot | null>(null);
  const [message, setMessage] = useState("");

  useEffect(() => applyThemePreference(themePreference), [themePreference]);
  const closeSettings = useCallback(() => setSettingsOpen(false), []);
  const copy = DESTINATION_COPY[activeDestination];

  const openProfile = async (): Promise<void> => {
    try {
      const opened = await client.chooseAndOpenProfile();
      if (opened !== null) {
        setEditor(opened);
        setActiveDestination("home");
        setMessage("");
      }
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    }
  };

  const newProfile = async (): Promise<void> => {
    try {
      const opened = await client.newProfile("untitled.csv");
      setEditor(opened);
      setActiveDestination("home");
      setMessage("");
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    }
  };

  let content;
  if (activeDestination === "home" && editor !== null) {
    content = <EditorWorkspace client={client} snapshot={editor} onSnapshot={setEditor} />;
  } else if (activeDestination === "home") {
    content = (
      <section className="shell-placeholder home-start" aria-labelledby="page-title">
        <h1 id="page-title">{t(copy.title)}</h1>
        <p data-testid="boot-state">{t(copy.detail)}</p>
        <div className="home-start-actions">
          <button className="primary-action" type="button" onClick={() => void newProfile()}>
            {t("Shell_NewProfile")}
          </button>
          <button type="button" onClick={() => void openProfile()}>
            {t("Shell_OpenAProfileFile")}
          </button>
        </div>
      </section>
    );
  } else {
    content = (
      <section className="shell-placeholder" aria-labelledby="page-title">
        <h1 id="page-title">{t(copy.title)}</h1>
        <p data-testid="boot-state">{t(copy.detail)}</p>
      </section>
    );
  }

  return (
    <>
      <AppShell
        activeDestination={activeDestination}
        onNavigate={setActiveDestination}
        themePreference={themePreference}
        onThemePreferenceChange={setThemePreference}
        onOpenSettings={() => setSettingsOpen(true)}
      >
        {content}
      </AppShell>
      <LiveRegion>{message}</LiveRegion>
      <ToastRegion messages={[]} />
      <Dialog
        open={settingsOpen}
        title={t("Shell_Settings")}
        onClose={closeSettings}
        actions={
          <button className="primary-action" type="button" data-autofocus onClick={closeSettings}>
            {t("Main_Done")}
          </button>
        }
      >
        <div className="settings-foundation">
          <label>
            <span>{t("Settings_Language")}</span>
            <select
              aria-label={t("Settings_Language")}
              value={preference}
              onChange={(event) => setPreference(event.currentTarget.value as LocalePreference)}
            >
              <option value="system">{t("Settings_LanguageSystem")}</option>
              {LOCALE_TAGS.map((tag) => (
                <option key={tag} value={tag}>{LOCALE_NAMES[tag]}</option>
              ))}
              {import.meta.env.DEV ? (
                <option value="qps-ploc">{t("Rewrite_PseudoLocaleName")}</option>
              ) : null}
            </select>
          </label>
          <p>{t("Settings_AppearanceHelp")}</p>
        </div>
      </Dialog>
    </>
  );
}

export function App({ client = DEFAULT_CLIENT }: AppProps = {}) {
  return (
    <I18nProvider>
      <LocalizedApp client={client} />
    </I18nProvider>
  );
}
''',
)

write(
    "src/main.tsx",
    r'''import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./app/App";
import { resolveQcmClient } from "./platform";
import "./styles/app.css";

const container = document.getElementById("root");
if (container === null) {
  throw new Error("index.html is missing the #root container");
}

async function mount(): Promise<void> {
  const client = await resolveQcmClient();
  createRoot(container as HTMLElement).render(
    <StrictMode>
      <App client={client} />
    </StrictMode>,
  );
}

void mount();
''',
)
replace_once(
    "src/styles/app.css",
    '@import "./tokens.css";\n',
    '@import "./tokens.css";\n@import "./editor.css";\n',
)
replace_once(
    "src/styles/app.css",
    '''.shell-placeholder p {\n  margin: 0;\n  color: var(--qcm-text-secondary);\n}\n''',
    '''.shell-placeholder p {\n  margin: 0;\n  color: var(--qcm-text-secondary);\n}\n\n.home-start-actions {\n  display: flex;\n  flex-wrap: wrap;\n  gap: var(--qcm-space-sm);\n  margin-top: var(--qcm-space-lg);\n}\n\n.home-start-actions button {\n  padding-inline: var(--qcm-space-lg);\n}\n''',
)

# Execution truth and ledgers.
replace_once(
    "docs/tauri-rewrite/implementation-status.md",
    '| TASK-037 localization migration | **IN PROGRESS** | candidate includes deterministic catalog/RTL/pseudo/error tests; exact-head gate pending | final manual locale/AT hardening later | generated from shipping RESX keyspace |',
    '| TASK-037 localization migration | **DONE** | **PASS** — exact-head run `33347003405` | final manual locale/AT hardening later | deterministic shipping RESX → React catalogs, pseudo, RTL, error localization |\n| TASK-038 editor parity UI | **IMPLEMENTING** | feature-branch tests/gate pending | final AT pass later | snapshot-driven modes/bindings/issues/raw-grid UI |',
)
replace_once(
    "docs/tauri-rewrite/implementation-status.md",
    'Finish and promote TASK-037 after its exact-head gate, then build TASK-038 editor parity UI on the proven integration head.',
    'Finish TASK-038 editor parity UI and promote only after its exact-head gate. TASK-040A local file workflow follows.',
)
replace_once(
    "docs/tauri-rewrite/49-implementation-checklist.md",
    '- [ ] TASK-037 Migrate all localization catalogs + pseudo-loc/RTL pipeline.',
    '- [x] TASK-037 Migrate all localization catalogs + pseudo-loc/RTL pipeline.',
)
replace_once(
    "docs/tauri-rewrite/ledgers/API_LEDGER.md",
    '| choose_and_open_profile | command | file-read via picker | native picker mints the only id | REGISTERED |',
    '| choose_and_open_profile | command | file-read via picker | native picker mints the only id | REGISTERED |\n| get_profile_snapshot | command | read-only state | opaque session id | REGISTERED |',
)
for api in [
    "open_device_profile",
    "list_devices",
    "refresh_devices",
    "choose_device_folder",
    "get_device_library",
    "prepare_install",
    "commit_install",
    "prepare_delete_device_profile",
    "commit_delete_device_profile",
    "open_device_preferences",
    "start_live_input",
    "stop_live_input",
]:
    path = "docs/tauri-rewrite/ledgers/API_LEDGER.md"
    text = read(path)
    lines = text.splitlines()
    changed = False
    for index, line in enumerate(lines):
        if line.startswith(f"| {api} |") and line.endswith("| PLANNED |"):
            lines[index] = line[:-len("PLANNED |") ] + "REGISTERED |"
            changed = True
            break
    if changed:
        write(path, "\n".join(lines) + "\n")
replace_once(
    "docs/tauri-rewrite/ledgers/API_LEDGER.md",
    '| qcm://devices-changed | event | low | native producer only | PLANNED |',
    '| subscribe_devices_changed | command+Channel | low | native invalidation producer only | REGISTERED |\n| unsubscribe_devices_changed | command | low | opaque subscription id | REGISTERED |\n| qcm://devices-changed | event | low | native producer only | PLANNED |',
)
replace_once(
    "docs/tauri-rewrite/ledgers/PORTING_LEDGER.md",
    '| `MainWindow.axaml` | shell/layout | React AppShell/Editor | REWRITE | component/E2E/AT | 5/6 | CONTRACTED |',
    '| `MainWindow.axaml` | shell/layout | React AppShell/Editor | REWRITE | component/E2E/AT | 5/6 | IMPLEMENTING |',
)
replace_once(
    "docs/tauri-rewrite/ledgers/PORTING_LEDGER.md",
    '| `RowControls.cs` | row editing controls | React editor | REWRITE | component | 6 | ASSESSED |',
    '| `RowControls.cs` | row editing controls | React editor | REWRITE | component | 6 | IMPLEMENTING |',
)
replace_once(
    "docs/tauri-rewrite/ledgers/PORTING_LEDGER.md",
    '| `ModesWindow.cs` | modes UX | React ModesPanel | REWRITE | E2E/AT | 6 | CONTRACTED |',
    '| `ModesWindow.cs` | modes UX | React ModesPanel | REWRITE | E2E/AT | 6 | IMPLEMENTING |',
)
replace_once(
    "docs/tauri-rewrite/ledgers/PORTING_LEDGER.md",
    '| `Localization.cs`, `Plural.cs` | locale runtime | frontend i18n | REWRITE | pseudo/RTL | 5 | ASSESSED |',
    '| `Localization.cs`, `Plural.cs` | locale runtime | frontend i18n | REWRITE | pseudo/RTL | 5 | PORTED |',
)
replace_once(
    "docs/tauri-rewrite/ledgers/PORTING_LEDGER.md",
    '| `Strings*.resx` | translations | generated frontend catalogs | CONVERT | key/placeholder | 5 | CONTRACTED |',
    '| `Strings*.resx` | translations | generated frontend catalogs | CONVERT | key/placeholder | 5 | PORTED |',
)
replace_once(
    "docs/tauri-rewrite/ledgers/BEHAVIOR_LEDGER.md",
    '| B-034 | A | all locales + RTL + pseudo | i18n CI | CONTRACTED |',
    '| B-034 | A | all locales + RTL + pseudo | i18n CI | PARITY-TESTED |',
)
replace_once(
    "docs/tauri-rewrite/ledgers/BEHAVIOR_LEDGER.md",
    '| B-037 | A | mode operations | mutation/E2E | CONTRACTED |',
    '| B-037 | A | mode operations | mutation/E2E | IMPLEMENTING |',
)
replace_once(
    "docs/tauri-rewrite/ledgers/TEST_LEDGER.md",
    '| T-013 | frontend editor | RTL/mock E2E | AT | PLANNED |',
    '| T-013 | frontend editor | `src/features/editor/EditorWorkspace.test.tsx` mock E2E + axe + keyboard | AT | IMPLEMENTED |',
)
replace_once(
    "docs/tauri-rewrite/ledgers/TEST_LEDGER.md",
    '| T-021 | i18n | key/pseudo/RTL | locales | PLANNED |',
    '| T-021 | i18n | generated key/placeholder/pseudo/RTL/error-code suite | locales | IMPLEMENTED |',
)

# Self-remove so the long-lived branch contains no write-capable patch tooling.
Path("scripts/task38_patch_tmp.py").unlink()
Path(".github/workflows/rewrite-task38-patch-tmp.yml").unlink()
