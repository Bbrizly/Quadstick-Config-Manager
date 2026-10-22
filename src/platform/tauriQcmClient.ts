/** The only frontend file allowed to import `@tauri-apps/*`. */
import { Channel, invoke } from "@tauri-apps/api/core";
import type { CommunityCatalog } from "./communityContracts";
import type { ConsoleClient } from "./consoleContracts";
import type { BridgeState, ControllerClient } from "./controllerContracts";
import type {
  AppSettings, AppSnapshot, CloseDisposition, CloseOutcome, DeletePlan, DeleteReceipt,
  DeviceInvalidation, DeviceLibrarySnapshot, DevicePresenceSnapshot, EditorOp, EditorSnapshot,
  InstallPlan, InstallProgress, InstallReceipt, LiveSnapshot, SaveReceipt, SettingsPatch, Subscription,
  UpdateResult, PendingCrashReport, CrashReportChoice, CrashResolveResult, PendingRescue,
  InstallCancelReason,
} from "./contracts";
import type { RenameDeviceProfileReceipt } from "./deviceRenameContracts";
import type {
  DriveBackupEvent, DriveBackupOutcome, DriveConflictChoice, DriveFile, DriveResolution, DriveShare, RestoreSummary,
} from "./driveContracts";
import type { FollowState, LatestCheck, LatestReplace, SheetsImport } from "./followContracts";
import type { GoogleAuthStatus } from "./googleContracts";
import type { CustomNameDrafts, LibraryCard, TemplateEntry, TemplateReceipt, TemplateRename } from "./libraryContracts";
import type { PreferenceCatalog } from "./preferenceContracts";
import { asQcmError, type QcmClient } from "./qcmClient";
import type { WorkbookExportReceipt, WorkbookImportReview } from "./workbookContracts";

interface NativeSubscription { readonly subscriptionId: string; }
async function call<T>(command: string, request?: unknown): Promise<T> {
  return callArgs<T>(command, request === undefined ? undefined : { request });
}
async function callArgs<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  try { return await invoke<T>(command, args); } catch (reason) { throw asQcmError(reason); }
}
function disposal(command: string, subscriptionId: string): Subscription {
  let disposed = false;
  return { dispose(): void {
    if (disposed) return;
    disposed = true;
    void invoke(command, { subscriptionId }).catch(() => undefined);
  } };
}

export class TauriQcmClient implements QcmClient {
  readonly console: ConsoleClient = {
    listConsolePorts: () => call("list_console_ports"),
    connectConsole: (portId) => call("connect_console", { portId }),
    closeConsole: () => call("close_console"),
    chooseConsoleProfile: () => call("choose_console_profile"),
    consoleSendProfile: (pickId, confirmDefaultCsv, confirmPreferencesCsv) =>
      call("console_send_profile", { pickId, confirmDefaultCsv, confirmPreferencesCsv }),
    consoleStrengthBegin: () => call("console_strength_begin"),
    consoleCalibrate: () => call("console_calibrate"),
    consoleSetThreshold: (puff, value) => call("console_set_threshold", { puff, value }),
    consoleStrengthEnd: () => call("console_strength_end"),
    recordStrengthMeasured: async (puff, sip) => {
      await call<boolean>("console_record_strength", { puff, sip });
    },
  };
  readonly controller: ControllerClient = {
    getControllerOptions: () => call("get_controller_options"),
    setControllerBridge: (on) => callArgs("set_controller_bridge", { on }),
    setMacGamepad: (on) => callArgs("set_mac_gamepad", { on }),
    subscribeControllerBridge: async (onState) => {
      const channel = new Channel<BridgeState | null>(onState);
      const native = await callArgs<NativeSubscription>("subscribe_controller_bridge", { onState: channel });
      return disposal("unsubscribe_controller_bridge", native.subscriptionId);
    },
  };
  getAppSnapshot(): Promise<AppSnapshot> { return call("get_app_snapshot"); }
  getSettings(): Promise<AppSettings> { return call("get_settings"); }
  updateSettings(expectedRevision: number, patch: SettingsPatch): Promise<AppSettings> { return call("update_settings", { expectedRevision, patch }); }
  checkForUpdate(currentVersion?: string): Promise<UpdateResult> {
    return call("check_for_update", currentVersion === undefined ? {} : { currentVersion });
  }
  openExternalUrl(url: string): Promise<void> { return call("open_external_url", { url }); }
  sendFeedback(text: string): Promise<void> { return call("send_feedback", { text }); }
  openPrivacyPolicy(): Promise<void> { return call("open_privacy_policy"); }
  async recordInstallCancelled(reason: InstallCancelReason): Promise<void> {
    await call<boolean>("record_install_cancelled", { reason });
  }
  getPendingCrashReport(): Promise<PendingCrashReport | null> {
    return call("get_pending_crash_report");
  }
  resolveCrashReport(reportId: string, choice: CrashReportChoice): Promise<CrashResolveResult> {
    return call("resolve_crash_report", { reportId, choice });
  }
  getPendingRescue(): Promise<PendingRescue | null> {
    return call("get_pending_rescue");
  }
  openRescueProfile(rescueId: string): Promise<EditorSnapshot> {
    return call("open_rescue_profile", { rescueId });
  }
  discardPendingRescues(): Promise<void> {
    return call("discard_pending_rescues");
  }
  newProfile(name: string): Promise<EditorSnapshot> { return call("new_profile", { name }); }
  chooseAndOpenProfile(): Promise<EditorSnapshot | null> { return call("choose_and_open_profile"); }
  listLibraryProfiles(): Promise<readonly LibraryCard[]> { return call("list_library_profiles"); }
  openLibraryProfile(id: string): Promise<EditorSnapshot> { return call("open_library_profile", { id }); }
  deleteLibraryProfile(id: string): Promise<void> { return call("delete_library_profile", { id }); }
  listTemplates(): Promise<readonly TemplateEntry[]> { return call("list_templates"); }
  openTemplate(id: string): Promise<EditorSnapshot> { return call("open_template", { id }); }
  saveTemplate(sessionId: string, expectedRevision: number, name: string): Promise<TemplateReceipt> {
    return call("save_template", { sessionId, expectedRevision, name });
  }
  renameTemplate(id: string, name: string): Promise<TemplateRename> { return call("rename_template", { id, name }); }
  deleteTemplate(id: string): Promise<void> { return call("delete_template", { id }); }
  deviceLocation(deviceId: string): Promise<string | null> { return call("device_location", { deviceId }); }
  getCustomNameDrafts(sessionId: string): Promise<CustomNameDrafts> { return call("get_custom_name_drafts", { sessionId }); }
  setCustomNameDrafts(sessionId: string, drafts: readonly (readonly [string, string])[]): Promise<CustomNameDrafts> {
    return call("set_custom_name_drafts", { sessionId, drafts });
  }
  getProfileSnapshot(sessionId: string): Promise<EditorSnapshot> { return call("get_profile_snapshot", { sessionId }); }
  applyEditorOps(sessionId: string, expectedRevision: number, ops: readonly EditorOp[]): Promise<EditorSnapshot> { return call("apply_editor_ops", { sessionId, expectedRevision, ops }); }
  undoEditor(sessionId: string, expectedRevision: number): Promise<EditorSnapshot> { return call("undo_editor", { sessionId, expectedRevision }); }
  saveProfile(sessionId: string, expectedRevision: number): Promise<SaveReceipt> { return call("save_profile", { sessionId, expectedRevision }); }
  saveProfileAs(sessionId: string, expectedRevision: number): Promise<SaveReceipt | null> { return call("save_profile_as", { sessionId, expectedRevision }); }
  closeProfile(sessionId: string, disposition: CloseDisposition): Promise<CloseOutcome> { return call("close_profile", { sessionId, disposition }); }
  chooseAndImportWorkbook(): Promise<WorkbookImportReview | null> { return call("choose_and_import_workbook"); }
  repairWorkbookTab(importId: string, tabIndex: number): Promise<WorkbookImportReview> { return call("repair_workbook_tab", { importId, tabIndex }); }
  acceptWorkbookImport(importId: string): Promise<EditorSnapshot> { return call("accept_workbook_import", { importId }); }
  cancelWorkbookImport(importId: string): Promise<void> { return call("cancel_workbook_import", { importId }); }
  exportProfileXlsx(sessionId: string, expectedRevision: number): Promise<WorkbookExportReceipt | null> { return call("export_profile_xlsx", { sessionId, expectedRevision }); }
  getPreferenceCatalog(): Promise<PreferenceCatalog> { return call("get_preference_catalog"); }
  importSheetsLink(url: string): Promise<SheetsImport> { return call("import_sheets_link", { url }); }
  getFollowState(sessionId: string): Promise<FollowState> { return call("get_follow_state", { sessionId }); }
  forgetFollowLink(sessionId: string): Promise<FollowState> { return call("forget_follow_link", { sessionId }); }
  checkLatestFromLink(sessionId: string, expectedRevision: number): Promise<LatestCheck> { return call("check_latest_from_link", { sessionId, expectedRevision }); }
  replaceWithLatest(planId: string): Promise<LatestReplace> { return call("replace_with_latest", { planId }); }
  takeLaunchProfileLink(): Promise<string | null> { return call("take_launch_profile_link"); }
  async subscribeProfileLinks(onLink: (profileId: string) => void): Promise<Subscription> {
    const channel = new Channel<string>(onLink);
    const native = await callArgs<NativeSubscription>("subscribe_profile_links", { onLink: channel });
    return disposal("unsubscribe_profile_links", native.subscriptionId);
  }
  async subscribeCloseRequests(onRequest: () => void): Promise<Subscription> {
    const channel = new Channel<boolean>(() => onRequest());
    const native = await callArgs<NativeSubscription>("subscribe_close_requests", { onRequest: channel });
    return disposal("unsubscribe_close_requests", native.subscriptionId);
  }
  ackCloseRequest(): Promise<void> { return call("ack_close_request"); }
  quitApp(): Promise<void> { return call("quit_app"); }
  openRegistryProfile(profileId: string): Promise<EditorSnapshot> { return call("open_registry_profile", { profileId }); }
  loadCommunityCatalog(refresh: boolean): Promise<CommunityCatalog> { return call("load_community_catalog", { refresh }); }
  importCommunityProfile(sheetId: string, csvName: string): Promise<WorkbookImportReview> { return call("import_community_profile", { sheetId, csvName }); }
  openCommunitySheet(sheetId: string): Promise<void> { return call("open_community_sheet", { sheetId }); }
  getGoogleAuthStatus(): Promise<GoogleAuthStatus> { return call("get_google_auth_status"); }
  connectGoogle(): Promise<GoogleAuthStatus> { return call("connect_google"); }
  disconnectGoogle(): Promise<GoogleAuthStatus> { return call("disconnect_google"); }
  backupProfileToDrive(sessionId: string, expectedRevision: number): Promise<DriveBackupOutcome> { return call("backup_profile_to_drive", { sessionId, expectedRevision }); }
  resolveDriveConflict(resolutionId: string, choice: DriveConflictChoice): Promise<DriveResolution> { return call("resolve_drive_conflict", { resolutionId, choice }); }
  async subscribeDriveBackups(onEvent: (event: DriveBackupEvent) => void): Promise<Subscription> {
    const channel = new Channel<DriveBackupEvent>(onEvent);
    const native = await callArgs<NativeSubscription>("subscribe_drive_backups", { onEvent: channel });
    return disposal("unsubscribe_drive_backups", native.subscriptionId);
  }
  listDriveBackups(): Promise<readonly DriveFile[]> { return call("list_drive_backups"); }
  restoreDriveBackup(cloudRef: string): Promise<WorkbookImportReview> { return call("restore_drive_backup", { cloudRef }); }
  restoreDriveBackups(cloudRefs: readonly string[]): Promise<RestoreSummary> { return call("restore_drive_backups", { cloudRefs }); }
  shareDriveProfile(sessionId: string, expectedRevision: number): Promise<DriveShare> { return call("share_drive_profile", { sessionId, expectedRevision }); }
  listDevices(): Promise<DevicePresenceSnapshot> { return call("list_devices"); }
  refreshDevices(): Promise<DevicePresenceSnapshot> { return call("refresh_devices"); }
  chooseDeviceFolder(): Promise<DevicePresenceSnapshot | null> { return call("choose_device_folder"); }
  getDeviceLibrary(deviceId: string): Promise<DeviceLibrarySnapshot> { return call("get_device_library", { deviceId }); }
  prepareInstall(sessionId: string, deviceId: string): Promise<InstallPlan> { return call("prepare_install", { sessionId, deviceId }); }
  commitInstall(planId: string, confirmationId?: string, onProgress: (progress: InstallProgress) => void = () => undefined): Promise<InstallReceipt> {
    const progress = new Channel<InstallProgress>(onProgress);
    return callArgs("commit_install", { request: { planId, confirmationId }, progress });
  }
  prepareDeleteDeviceProfile(deviceId: string, expectedGeneration: number, name: string): Promise<DeletePlan> { return call("prepare_delete_device_profile", { deviceId, expectedGeneration, name }); }
  commitDeleteDeviceProfile(planId: string, confirmationId: string): Promise<DeleteReceipt> { return call("commit_delete_device_profile", { planId, confirmationId }); }
  renameDeviceProfile(deviceId: string, expectedGeneration: number, from: string, to: string): Promise<RenameDeviceProfileReceipt> { return call("rename_device_profile", { deviceId, expectedGeneration, from, to }); }
  openDeviceProfile(deviceId: string, expectedGeneration: number, name: string): Promise<EditorSnapshot> { return call("open_device_profile", { deviceId, expectedGeneration, name }); }
  openDevicePreferences(deviceId: string, expectedGeneration: number, createIfMissing = false): Promise<EditorSnapshot> { return call("open_device_preferences", { deviceId, expectedGeneration, createIfMissing }); }
  async startLiveInput(onFrame: (frame: LiveSnapshot) => void): Promise<Subscription> {
    const channel = new Channel<LiveSnapshot>(onFrame);
    const native = await callArgs<NativeSubscription>("start_live_input", { onFrame: channel });
    return disposal("stop_live_input", native.subscriptionId);
  }
  async subscribeDevicesChanged(onChanged: (event: DeviceInvalidation) => void): Promise<Subscription> {
    const channel = new Channel<DeviceInvalidation>(onChanged);
    const native = await callArgs<NativeSubscription>("subscribe_devices_changed", { onChanged: channel });
    return disposal("unsubscribe_devices_changed", native.subscriptionId);
  }
}

export const TAURI_COMMANDS = [
  "list_library_profiles", "open_library_profile", "delete_library_profile",
  "list_templates", "open_template", "save_template", "rename_template", "delete_template",
  "device_location", "get_custom_name_drafts", "set_custom_name_drafts",
  "get_app_snapshot", "get_settings", "update_settings", "new_profile", "choose_and_open_profile",
  "get_profile_snapshot", "apply_editor_ops", "undo_editor", "save_profile", "save_profile_as",
  "close_profile", "choose_and_import_workbook", "repair_workbook_tab", "accept_workbook_import",
  "cancel_workbook_import", "export_profile_xlsx", "get_preference_catalog", "load_community_catalog",
  "import_community_profile", "open_community_sheet", "get_google_auth_status", "connect_google",
  "disconnect_google", "backup_profile_to_drive", "resolve_drive_conflict", "list_drive_backups",
  "restore_drive_backup", "share_drive_profile", "subscribe_drive_backups", "unsubscribe_drive_backups", "list_devices", "refresh_devices",
  "choose_device_folder", "get_device_library", "prepare_install", "commit_install",
  "prepare_delete_device_profile", "commit_delete_device_profile", "rename_device_profile",
  "open_device_profile", "open_device_preferences", "start_live_input", "stop_live_input",
  "subscribe_devices_changed", "unsubscribe_devices_changed",
  "check_for_update", "open_external_url", "send_feedback", "open_privacy_policy",
  "record_install_cancelled",
  "get_pending_crash_report", "resolve_crash_report",
  "get_pending_rescue", "open_rescue_profile", "discard_pending_rescues",
  "list_console_ports", "connect_console", "close_console", "choose_console_profile",
  "console_send_profile", "console_strength_begin", "console_calibrate", "console_set_threshold",
  "console_strength_end", "console_record_strength",
  "take_launch_profile_link", "open_registry_profile", "subscribe_profile_links",
  "unsubscribe_profile_links", "subscribe_close_requests", "unsubscribe_close_requests", "ack_close_request",
  "quit_app",
  "get_controller_options", "set_controller_bridge", "set_mac_gamepad",
  "subscribe_controller_bridge", "unsubscribe_controller_bridge",
  "import_sheets_link", "get_follow_state", "forget_follow_link", "check_latest_from_link",
  "replace_with_latest", "restore_drive_backups",
] as const;
