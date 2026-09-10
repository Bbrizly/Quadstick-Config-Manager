# Frontend architecture

## Selected stack

- React 19.2
- TypeScript `strict`
- Vite 8.x (pin current supported patch at implementation)
- CSS variables + CSS modules/plain scoped CSS; do not import a giant component framework for parity work
- Vitest + React Testing Library + axe integration
- i18next/react-i18next or equivalent only after a translation-conversion spike proves plural/RTL/pseudo-loc behavior

## App shape

QCM is a desktop application with a small number of shell surfaces; **do not introduce a URL router unless deep-link/history requirements appear**. Start with typed shell state:

```ts
type AppView =
  | { kind: 'home' }
  | { kind: 'editor'; sessionId: string }
  | { kind: 'deviceSettings'; deviceId: string }
  | { kind: 'community' }
  | { kind: 'settings' };
```

Dialogs/popovers are local feature state, not routes.

## Feature folders

```text
src/features/
  editor/
    EditorPage.tsx
    ModeRail.tsx
    DeviceVisualizer.tsx
    BindingEditor.tsx
    RawGridView.tsx
    IssuesPanel.tsx
    editorController.ts
  device/
    DeviceLibrary.tsx
    InstallDialog.tsx
    DeviceStatus.tsx
  live-input/
    useLiveInput.ts
    LiveStatus.tsx
  device-settings/
  import/
  community/
  backup/
  settings/
  agent/
```

Components never import `@tauri-apps/*` directly.

## Controller pattern

Keep domain orchestration out of visual components without recreating MVVM boilerplate. Each feature can expose a controller hook/service that calls `QcmClient`, handles request lifecycle and returns plain view state/actions.

```ts
const editor = useEditorController(sessionId);
return <EditorPage model={editor.model} actions={editor.actions} />;
```

This makes components renderable against `MockQcmClient` in browser tests.

## Data fetching/state

Do not introduce React Query/Redux/Zustand by default. QCM's native backend is local and authoritative. Implement a tiny resource layer first:

- command on mount/explicit refresh;
- cancellation/ignore-late-result token;
- generation/revision checks;
- local loading/error states;
- low-frequency event invalidation.

Adopt a library only when duplicated cache/invalidation complexity is demonstrated and record ADR change.

## Error presentation

`QcmErrorDto.code` selects recovery UX. `message` is safe default localized/fallback text. Frontend should map stable codes to localized strings when user wording needs translation, while diagnostics keep native cause separately.

## No giant React MainWindow

The target must not recreate `MainWindow.axaml.cs` as `App.tsx`. Enforce soft limits in review:
- feature components should have one primary responsibility;
- native orchestration belongs in core;
- pure formatting/domain rules belong in Rust;
- visual mapping helpers can be TS if they cannot affect persisted/device semantics.

## StrictMode

Develop in React StrictMode. All subscriptions/live streams/dialog side effects must tolerate effect setup/cleanup twice in development without duplicate native streams.