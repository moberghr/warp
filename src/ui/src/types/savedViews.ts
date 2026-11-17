// Saved views (§8.34): a dashboard page plus its URL query string under a name, personal (listed to its author only)
// or shared (listed to everyone, who may edit and delete it).

/** The pages a view can be saved on; mirrors SavedViewValidation.Pages on the server. */
export type SavedViewPage = 'metric' | 'issues' | 'jobs' | 'jobs-by-type';

export interface SavedView {
  id: number;
  name: string;
  page: SavedViewPage;
  /** The instrument (metric), job state (jobs) or job type (jobs-by-type) the page's path names; null for issues. */
  target: string | null;
  /** The page's URL query string, without `?`. */
  query: string;
  shared: boolean;
  author: string;
  createdAt: string;
  updatedAt: string;
}

/** GET /views: the caller (as the server names them, `anonymous` on an open dashboard) and the views they can see. */
export interface SavedViewList {
  user: string;
  views: SavedView[];
}

/** POST /views: id 0 creates. No author: the server takes it from the signed-in dashboard user. */
export interface SavedViewUpsert {
  id: number;
  name: string;
  page: SavedViewPage;
  target: string | null;
  query: string;
  shared: boolean;
}
