// Engine for the project-administration hub page: the page's state, its
// transitions, and every decision about what to request and what to show.
// Pure functions of their arguments -- no browser, network or host authority
// (Limen's engine boundary; see limen.config.json). The kernel
// (web-hub/app.ts) performs the requests, reads and writes the DOM, and feeds
// results back in here.
//
// The hub itself decides nothing about work-item legality: every create/list
// request is a pass-through to a spoke repository's own `./ros`, run by the
// server. Errors shown are exactly what that repository's CLI said.

export type RepoEntry = {
  id: string;
  name: string;
  path: string;
  registeredAt: string;
};

export type AggregatedRow = {
  repoId: string;
  repoName: string;
  id?: string;
  title?: string;
  status?: string;
  tags?: string[];
  priority?: string | null;
  error?: string;
};

export type Filter = {
  readonly repo: string;
  readonly tag: string;
  readonly status: string;
};

export type State = {
  readonly repos: readonly RepoEntry[];
  readonly rows: readonly AggregatedRow[];
  readonly filter: Filter;
  readonly reposError: string | null;
  readonly createError: string | null;
  readonly listError: string | null;
};

export const initialState: State = Object.freeze({
  repos: [],
  rows: [],
  filter: { repo: "", tag: "", status: "" },
  reposError: null,
  createError: null,
  listError: null
});

// ---------------------------------------------------------------------------
// Transitions: (state, input) -> state.
// ---------------------------------------------------------------------------

export const reposLoaded = (state: State, repos: readonly RepoEntry[]): State => ({ ...state, repos, reposError: null });
export const reposFailed = (state: State, message: string): State => ({ ...state, reposError: message });
export const registered = (state: State): State => ({ ...state, reposError: null });

export const workLoaded = (state: State, rows: readonly AggregatedRow[]): State => ({ ...state, rows, listError: null });
export const workFailed = (state: State, message: string): State => ({ ...state, listError: message });

export const created = (state: State): State => ({ ...state, createError: null });
export const createFailed = (state: State, message: string): State => ({ ...state, createError: message });

export const repoFilterChanged = (state: State, repo: string): State => ({ ...state, filter: { ...state.filter, repo } });
export const tagFilterChanged = (state: State, tag: string): State => ({ ...state, filter: { ...state.filter, tag } });
export const statusFilterChanged = (state: State, status: string): State =>
  ({ ...state, filter: { ...state.filter, status } });
export const filtersCleared = (state: State): State => ({ ...state, filter: { repo: "", tag: "", status: "" } });

// ---------------------------------------------------------------------------
// Requests, described as data. The kernel performs them.
// ---------------------------------------------------------------------------

export type JsonValue = string | number | boolean | null | readonly JsonValue[] | { readonly [key: string]: JsonValue | undefined };

export type Request = {
  readonly method: "GET" | "POST" | "DELETE";
  readonly path: string;
  readonly json?: JsonValue;
  /** Multipart text fields; the kernel appends the selected files after them. */
  readonly formFields?: readonly (readonly [string, string])[];
};

export type CreateInput = {
  readonly repoId: string;
  readonly title: string;
  readonly tags: string[];
  readonly priority: string;
  readonly description: string;
};

export function parseTags(raw: string): string[] {
  return raw.split(",").map((tag) => tag.trim()).filter(Boolean);
}

export function buildWorkQuery(filter: Filter): string {
  const params = new URLSearchParams();
  if (filter.repo) params.set("repo", filter.repo);
  if (filter.tag.trim()) {
    for (const tag of parseTags(filter.tag)) params.append("tag", tag);
  }
  if (filter.status) params.set("status", filter.status);
  const query = params.toString();
  return query ? `?${query}` : "";
}

export const requests = {
  repos: (): Request => ({ method: "GET", path: "/api/repos" }),
  register: (repoPath: string, name: string): Request =>
    ({ method: "POST", path: "/api/repos", json: { path: repoPath, name: name || undefined } }),
  unregister: (id: string): Request => ({ method: "DELETE", path: `/api/repos/${encodeURIComponent(id)}` }),
  work: (filter: Filter): Request => ({ method: "GET", path: `/api/work${buildWorkQuery(filter)}` }),
  /** JSON when nothing is attached; multipart (fields, then files) when files are. */
  createWork: (input: CreateInput, withFiles: boolean): Request => {
    const path = `/api/repos/${encodeURIComponent(input.repoId)}/work`;
    if (!withFiles) {
      return { method: "POST", path, json: { title: input.title, tags: input.tags, priority: input.priority, description: input.description } };
    }
    return {
      method: "POST",
      path,
      formFields: [
        ["title", input.title],
        ["tags", input.tags.join(",")],
        ["priority", input.priority],
        ["description", input.description]
      ]
    };
  }
};

/** The message shown for a failed request: the server's `error`, or the status. */
export function responseError(status: number, body: unknown): string {
  return typeof body === "object" && body && "error" in body
    ? String((body as { error: unknown }).error)
    : `request failed (${status})`;
}

// ---------------------------------------------------------------------------
// Decisions about input.
// ---------------------------------------------------------------------------

/** A registration, or null when no path was given. */
export function registerInput(raw: { path: string; name: string }): { path: string; name: string } | null {
  const repoPath = raw.path.trim();
  if (!repoPath) return null;
  return { path: repoPath, name: raw.name.trim() };
}

/** A new work item, or null when no repository or title was given. */
export function createInput(raw: { repoId: string; title: string; tags: string; priority: string; description: string }): CreateInput | null {
  const title = raw.title.trim();
  if (!raw.repoId || !title) return null;
  return { repoId: raw.repoId, title, tags: parseTags(raw.tags), priority: raw.priority, description: raw.description.trim() };
}

/** A file's associated name: the optional override, else the selected file's own name. */
export function uploadName(override: string, fileName: string): string {
  return override.trim() || fileName;
}

export function unregisterConfirmation(repo: RepoEntry): string {
  return `Unregister ${repo.name} (${repo.path})? This only removes it from the hub -- the repository itself is unaffected.`;
}

// ---------------------------------------------------------------------------
// Projection.
// ---------------------------------------------------------------------------

/** The selection to keep after the repository list changes, or null if it no longer exists. */
export function retainedSelection(repos: readonly RepoEntry[], current: string): string | null {
  return repos.some((repo) => repo.id === current) ? current : null;
}
