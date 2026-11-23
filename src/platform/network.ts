/**
 * Off means gone. A host that runs without the network hands the UI a client
 * whose network methods are undefined and whose capabilities say so, and the
 * UI hides those controls rather than greying them. Native refuses the same
 * commands on its own (`NETWORK_COMMANDS`, `src-tauri/src/host.rs`).
 */
import type { AppSnapshot } from "./contracts";
import type { QcmClient } from "./qcmClient";

/** Every optional client method that reaches the internet, and the native
 *  command(s) behind it. A test holds this to the Rust list. */
export const NETWORK_METHODS = {
  loadCommunityCatalog: ["load_community_catalog"],
  importCommunityProfile: ["import_community_profile"],
  openCommunitySheet: ["open_community_sheet"],
  getGoogleAuthStatus: ["get_google_auth_status"],
  connectGoogle: ["connect_google"],
  disconnectGoogle: ["disconnect_google"],
  backupProfileToDrive: ["backup_profile_to_drive"],
  resolveDriveConflict: ["resolve_drive_conflict"],
  listDriveBackups: ["list_drive_backups"],
  restoreDriveBackup: ["restore_drive_backup"],
  restoreDriveBackups: ["restore_drive_backups"],
  shareDriveProfile: ["share_drive_profile"],
  subscribeDriveBackups: ["subscribe_drive_backups", "unsubscribe_drive_backups"],
  checkForUpdate: ["check_for_update"],
  openExternalUrl: ["open_external_url"],
  openPrivacyPolicy: ["open_privacy_policy"],
  openContactLink: ["open_contact_link"],
  sendFeedback: ["send_feedback"],
  openRegistryProfile: ["open_registry_profile"],
  importSheetsLink: ["import_sheets_link"],
  checkLatestFromLink: ["check_latest_from_link"],
  replaceWithLatest: ["replace_with_latest"],
} as const satisfies Partial<Record<keyof QcmClient, readonly string[]>>;

export type NetworkMethod = keyof typeof NETWORK_METHODS;

/** `client` without any network method; its snapshot reports no network features. */
export function withoutNetwork(client: QcmClient): QcmClient {
  const gone = new Set<PropertyKey>(Object.keys(NETWORK_METHODS));
  const getAppSnapshot = async (): Promise<AppSnapshot> => {
    const snapshot = await client.getAppSnapshot();
    return {
      ...snapshot,
      capabilities: {
        ...snapshot.capabilities,
        communityCatalog: false,
        googleBackup: false,
        usageData: false,
      },
    };
  };
  return new Proxy(client, {
    get(target, key) {
      if (gone.has(key)) return undefined;
      if (key === "getAppSnapshot") return getAppSnapshot;
      const value: unknown = Reflect.get(target, key, target);
      return typeof value === "function" ? (value as (...args: unknown[]) => unknown).bind(target) : value;
    },
    has(target, key) {
      return !gone.has(key) && Reflect.has(target, key);
    },
  });
}
