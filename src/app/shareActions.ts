import type { QcmClient } from "../platform";

/** Whether the editor's Share flyout would hold anything for this client:
 *  Drive (`ProfileDriveActions`), the followed sheet (`FollowLinkActions`),
 *  or the .xlsx export. With none of them the flyout is not shown. */
export function hasShareActions(client: QcmClient): boolean {
  return (
    client.backupProfileToDrive !== undefined
    || (client.getFollowState !== undefined
      && client.checkLatestFromLink !== undefined
      && client.replaceWithLatest !== undefined)
    || client.exportProfileXlsx !== undefined
  );
}
