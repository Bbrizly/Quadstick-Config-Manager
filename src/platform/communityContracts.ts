import type { WorkbookImportReview } from "./workbookContracts";

export interface CommunityProfile {
  readonly name: string;
  readonly sheetId: string;
  readonly csvName: string;
  readonly connection: string;
  readonly notes: string;
  readonly pointer: string;
}

export interface CommunityCatalog {
  readonly profiles: readonly CommunityProfile[];
  readonly fromCache: boolean;
  readonly skippedRows: number;
}

export interface CommunityClient {
  loadCommunityCatalog(refresh: boolean): Promise<CommunityCatalog>;
  importCommunityProfile(sheetId: string, csvName: string): Promise<WorkbookImportReview>;
  openCommunitySheet(sheetId: string): Promise<void>;
}
