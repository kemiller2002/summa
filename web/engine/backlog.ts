// Engine for the work-backlog page: the page's state, its transitions, and
// every decision about what to request and what to show. Pure functions of
// their arguments -- no browser, network or host authority (Limen's engine
// boundary; see limen.config.json). The kernel (web/app.ts) performs the
// requests, reads and writes the DOM, and feeds results back in here.
//
// Legality of work-item transitions is still owned by the server (and the
// ros_cli.mjs it calls); this engine only maps the actions the server says
// are allowed onto the controls the page offers.

export type LiveWorkItem = {
  state: string;
  semanticState: string;
  allowedActions: string[];
};

export type Attachment = {
  id: string;
  name: string;
  size: number;
  contentType: string | null;
  uploadedAt: string;
};

export type WorkRow = {
  id: string;
  title: string;
  description: string | null;
  tags: string[];
  priority: "high" | "medium" | "low" | null;
  status: string;
  blockedReason?: string;
  backlogActions: string[];
  attachments: Attachment[];
  liveWorkItem: LiveWorkItem | null;
  detail?: string | null;
};

export type Filter = {
  readonly tag: string;
  readonly status: string;
};

export type State = {
  readonly rows: readonly WorkRow[];
  readonly filter: Filter;
  readonly selectedId: string | null;
  readonly error: string | null;
};

export const initialState: State = Object.freeze({
  rows: [],
  filter: { tag: "", status: "" },
  selectedId: null,
  error: null
});

// ---------------------------------------------------------------------------
// Transitions: (state, input) -> state.
// ---------------------------------------------------------------------------

export const rowsLoaded = (state: State, rows: readonly WorkRow[]): State => ({ ...state, rows, error: null });

export const requestFailed = (state: State, message: string): State => ({ ...state, error: message });

export const rowToggled = (state: State, id: string): State =>
  ({ ...state, selectedId: state.selectedId === id ? null : id });

export const tagFilterChanged = (state: State, tag: string): State => ({ ...state, filter: { ...state.filter, tag } });

export const statusFilterChanged = (state: State, status: string): State =>
  ({ ...state, filter: { ...state.filter, status } });

export const filtersCleared = (state: State): State => ({ ...state, filter: { tag: "", status: "" } });

// ---------------------------------------------------------------------------
// Requests, described as data. The kernel performs them.
// ---------------------------------------------------------------------------

export type JsonValue = string | number | boolean | null | readonly JsonValue[] | { readonly [key: string]: JsonValue };

export type Request = {
  readonly method: "GET" | "POST";
  readonly path: string;
  readonly json?: JsonValue;
};

export type Evidence = { readonly type: string; readonly path: string };
export type UpdateInput = { readonly title: string; readonly description: string; readonly tags: string[]; readonly priority: string };
export type AddInput = UpdateInput;
export type CompletionInput = { readonly evidence: Evidence[]; readonly conclusion: string | null };

export function parseTags(raw: string): string[] {
  return raw.split(",").map((tag) => tag.trim()).filter(Boolean);
}

export function buildQuery(filter: Filter): string {
  const params = new URLSearchParams();
  if (filter.tag.trim()) {
    for (const tag of parseTags(filter.tag)) params.append("tag", tag);
  }
  if (filter.status) params.set("status", filter.status);
  const query = params.toString();
  return query ? `?${query}` : "";
}

const item = (id: string, suffix: string): string => `/api/work/${encodeURIComponent(id)}/${suffix}`;

export const requests = {
  list: (filter: Filter): Request => ({ method: "GET", path: `/api/work${buildQuery(filter)}` }),
  status: (): Request => ({ method: "GET", path: "/api/status" }),
  add: (input: AddInput): Request =>
    ({ method: "POST", path: "/api/work", json: { title: input.title, tags: input.tags, priority: input.priority, description: input.description } }),
  update: (id: string, input: UpdateInput): Request =>
    ({ method: "POST", path: item(id, "update"), json: { title: input.title, description: input.description, tags: input.tags, priority: input.priority } }),
  // The kernel attaches the selected files as multipart form data.
  uploadAttachments: (id: string): Request => ({ method: "POST", path: item(id, "attachments") }),
  ready: (id: string): Request => ({ method: "POST", path: item(id, "ready") }),
  block: (id: string, reason: string): Request => ({ method: "POST", path: item(id, "block"), json: { reason } }),
  abandon: (id: string, reason: string): Request => ({ method: "POST", path: item(id, "abandon"), json: { reason } }),
  start: (id: string, type: string): Request => ({ method: "POST", path: item(id, "start"), json: { type } }),
  resume: (id: string): Request => ({ method: "POST", path: item(id, "resume") }),
  complete: (id: string, input: CompletionInput): Request =>
    ({ method: "POST", path: item(id, "complete"), json: { evidence: input.evidence, conclusion: input.conclusion } })
};

/** The message shown for a failed request: the server's `error`, or the status. */
export function responseError(status: number, body: unknown): string {
  return typeof body === "object" && body && "error" in body
    ? String((body as { error: unknown }).error)
    : `request failed (${status})`;
}

// ---------------------------------------------------------------------------
// Decisions about input: what the page submits, given what the user typed.
// ---------------------------------------------------------------------------

/** A new item, or null when there is nothing to submit (no title). */
export function addInput(raw: { title: string; description: string; tags: string; priority: string }): AddInput | null {
  const title = raw.title.trim();
  if (!title) return null;
  return { title, description: raw.description.trim(), tags: parseTags(raw.tags), priority: raw.priority };
}

export function updateDefaults(row: WorkRow): { title: string; description: string; tags: string; priority: string } {
  return { title: row.title, description: row.description ?? "", tags: row.tags.join(", "), priority: row.priority ?? "medium" };
}

/** An edit of `row`; a title the page could not read keeps the row's own title. */
export function updateInput(
  raw: { title: string | null; description: string; tags: string; priority: string },
  row: WorkRow
): UpdateInput {
  return {
    title: raw.title === null ? row.title : raw.title.trim(),
    description: raw.description.trim(),
    tags: parseTags(raw.tags),
    priority: raw.priority
  };
}

export function completionInput(rows: readonly { type: string; path: string }[], conclusion: string): CompletionInput {
  const evidence = rows
    .map((row) => ({ type: row.type.trim(), path: row.path.trim() }))
    .filter((row) => row.type && row.path);
  const trimmed = conclusion.trim();
  return { evidence, conclusion: trimmed || null };
}

/** A file's associated name: the optional override, else the selected file's own name. */
export function uploadName(override: string, fileName: string): string {
  return override.trim() || fileName;
}

// ---------------------------------------------------------------------------
// Projection: what the page shows, as named values.
// ---------------------------------------------------------------------------

export type RowActionKind = "show" | "ready" | "block" | "start" | "abandon" | "resume" | "complete" | "edit" | "attach";
export type RowAction = { readonly kind: RowActionKind; readonly label: string };

/** The controls a row offers: live-work actions when it is in flight, backlog actions otherwise. */
export function rowActions(row: WorkRow): RowAction[] {
  const actions: RowAction[] = [{ kind: "show", label: "Show" }];
  if (row.liveWorkItem) {
    for (const action of row.liveWorkItem.allowedActions) {
      if (action === "block") actions.push({ kind: "block", label: "Block" });
      if (action === "resume") actions.push({ kind: "resume", label: "Resume" });
      if (action === "complete") actions.push({ kind: "complete", label: "Complete" });
    }
  } else {
    for (const action of row.backlogActions) {
      if (action === "ready") actions.push({ kind: "ready", label: "Mark ready" });
      if (action === "block") actions.push({ kind: "block", label: "Block" });
      if (action === "start") actions.push({ kind: "start", label: "Start" });
      if (action === "abandon") actions.push({ kind: "abandon", label: "Abandon" });
    }
  }
  actions.push({ kind: "edit", label: "Edit" });
  actions.push({ kind: "attach", label: "Attach" });
  return actions;
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export type AttachmentView = { readonly href: string; readonly name: string; readonly sizeLabel: string };
export type DetailView = {
  readonly description: string;
  readonly attachments: readonly AttachmentView[];
  readonly raw: string;
};

/** The detail panel for the selected row, or null when nothing is selected. */
export function detailView(state: State): DetailView | null {
  const row = state.selectedId ? state.rows.find((candidate) => candidate.id === state.selectedId) ?? null : null;
  if (!row) return null;
  const raw = JSON.stringify(row, null, 2);
  return {
    description: row.description || "No description.",
    attachments: row.attachments.map((attachment) => ({
      href: `/api/work/${encodeURIComponent(row.id)}/attachments/${encodeURIComponent(attachment.id)}`,
      name: attachment.name,
      sizeLabel: ` (${formatSize(attachment.size)})`
    })),
    raw: row.detail ? `${raw}\n\n${row.detail}` : raw
  };
}

export type RepositoryStatus = { repository: string; protocolVersion: string; validation: string };

export function repositoryLabel(status: RepositoryStatus): string {
  return `${status.repository} · protocol ${status.protocolVersion} · validation ${status.validation}`;
}
