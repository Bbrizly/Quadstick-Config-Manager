/** The only frontend file allowed to import `@tauri-apps/*`. */
import { Channel, invoke } from "@tauri-apps/api/core";
import type { CommunityCatalog } from "./communityContracts";
import type {
  AppSettings, AppSnapshot, CloseDisposition, CloseOutcome, DeletePlan, DeleteReceipt,
  DeviceInvalidation, DeviceLibrarySnapshot, DevicePresenceSnapshot, EditorOp, EditorSnapshot,
  InstallPlan, InstallProgress, InstallReceipt, LiveSnapshot, SaveReceipt, SettingsPatch, Subscription,
} from "./contracts";
import type { RenameDeviceProfileReceipt } from "./deviceRenameContracts";
import type {
  DriveBackupOutcome, DriveConflictChoice, DriveFile, DriveResolution, DriveShare,
} from "./driveContracts";
import type { GoogleAuthStatus } from "./googleContracts";
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
  getAppSnapshot(): Promise<AppSnapshot> { return call("get_app_snapshot"); }
  getSettings(): Promise<AppSettings> { return call("get_settings"); }
  updateSettings(expectedRevision: number, patch: SettingsPatch): Promise<AppSettings> { return call("update_settings", { expectedRevision, patch }); }
  newProfile(name: string): Promise<EditorSnapshot> { return call("new_profile", { name }); }
  chooseAndOpenProfile(): Promise<EditorSnapshot | null> { return call("choose_and_open_profile"); }
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
  loadCommunityCatalog(refresh: boolean): Promise<CommunityCatalog> { return call("load_community_catalog", { refresh }); }
  importCommunityProfile(sheetId: string, csvName: string): Promise<WorkbookImportReview> { return call("import_community_profile", { sheetId, csvName }); }
  openCommunitySheet(sheetId: string): Promise<void> { return call("open_community_sheet", { sheetId }); }
  getGoogleAuthStatus(): Promise<GoogleAuthStatus> { return call("get_google_auth_status"); }
  connectGoogle(): Promise<GoogleAuthStatus> { return call("connect_google"); }
  disconnectGoogle(): Promise<GoogleAuthStatus> { return call("disconnect_google"); }
  backupProfileToDrive(sessionId: string, expectedRevision: number): Promise<DriveBackupOutcome> { return call("backup_profile_to_drive", { sessionId, expectedRevision }); }
  resolveDriveConflict(resolutionId: string, choice: DriveConflictChoice): Promise<DriveResolution> { return call("resolve_drive_conflict", { resolutionId, choice }); }
  listDriveBackups(): Promise<readonly DriveFile[]> { return call("list_drive_backups"); }
  restoreDriveBackup(cloudRef: string): Promise<WorkbookImportReview> { return call("restore_drive_backup", { cloudRef }); }
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
  openDevicePreferences(deviceId: string, expectedGeneration: number): Promise<EditorSnapshot> { return call("open_device_preferences", { deviceId, expectedGeneration }); }
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
  "get_app_snapshot", "get_settings", "update_settings", "new_profile", "choose_and_open_profile",
  "get_profile_snapshot", "apply_editor_ops", "undo_editor", "save_profile", "save_profile_as",
  "close_profile", "choose_and_import_workbook", "repair_workbook_tab", "accept_workbook_import",
  "cancel_workbook_import", "export_profile_xlsx", "get_preference_catalog", "load_community_catalog",
  "import_community_profile", "open_community_sheet", "get_google_auth_status", "connect_google",
  "disconnect_google", "backup_profile_to_drive", "resolve_drive_conflict", "list_drive_backups",
  "restore_drive_backup", "share_drive_profile", "list_devices", "refresh_devices",
  "choose_device_folder", "get_device_library", "prepare_install", "commit_install",
  "prepare_delete_device_profile", "commit_delete_device_profile", "rename_device_profile",
  "open_device_profile", "open_device_preferences", "start_live_input", "stop_live_input",
  "subscribe_devices_changed", "unsubscribe_devices_changed",
] as const;
