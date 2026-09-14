// Purely functional, framework-free client for the project-administration
// hub. Same discipline as web/app.ts: one state value, one setState that
// re-renders, pure (state) -> DOM functions, no two-way binding. The hub
// itself decides nothing about work-item legality -- every create/list
// request is a thin pass-through to a spoke repository's own `./ros`, run
// by the server. Errors shown here are exactly what that repository's CLI
// said.

type RepoEntry = {
  id: string;
  name: string;
  path: string;
  registeredAt: string;
};

type AggregatedRow = {
  repoId: string;
  repoName: string;
  id?: string;
  title?: string;
  status?: string;
  tags?: string[];
  priority?: string | null;
  error?: string;
};

type Filter = {
  repo: string;
  tag: string;
  status: string;
};

type State = {
  readonly repos: readonly RepoEntry[];
  readonly rows: readonly AggregatedRow[];
  readonly filter: Filter;
  readonly reposError: string | null;
  readonly createError: string | null;
  readonly listError: string | null;
};

const initialState: State = Object.freeze({
  repos: [],
  rows: [],
  filter: { repo: "", tag: "", status: "" },
  reposError: null,
  createError: null,
  listError: null
});

let state: State = initialState;

function setState(patch: Partial<State>): void {
  state = { ...state, ...patch };
  render(state);
}

// ---------------------------------------------------------------------------
// Effects
// ---------------------------------------------------------------------------

async function apiRequest<T>(path: string, init?: RequestInit): Promise<T> {
  const isForm = init?.body instanceof FormData;
  const response = await fetch(path, {
    ...init,
    headers: isForm ? (init?.headers ?? {}) : { "Content-Type": "application/json", ...(init?.headers ?? {}) }
  });
  const body: unknown = await response.json();
  if (!response.ok) {
    const message = typeof body === "object" && body && "error" in body ? String((body as { error: unknown }).error) : `request failed (${response.status})`;
    throw new Error(message);
  }
  return body as T;
}

function buildWorkQuery(filter: Filter): string {
  const params = new URLSearchParams();
  if (filter.repo) params.set("repo", filter.repo);
  if (filter.tag.trim()) {
    for (const tag of filter.tag.split(",").map((value) => value.trim()).filter(Boolean)) params.append("tag", tag);
  }
  if (filter.status) params.set("status", filter.status);
  const query = params.toString();
  return query ? `?${query}` : "";
}

type CreateInput = {
  title: string;
  tags: string[];
  priority: string;
  description: string;
  files: { blob: File; name: string }[];
};

const api = {
  repos: (): Promise<RepoEntry[]> => apiRequest("/api/repos"),
  register: (repoPath: string, name: string): Promise<RepoEntry> =>
    apiRequest("/api/repos", { method: "POST", body: JSON.stringify({ path: repoPath, name: name || undefined }) }),
  unregister: (id: string): Promise<RepoEntry> => apiRequest(`/api/repos/${encodeURIComponent(id)}`, { method: "DELETE" }),
  work: (filter: Filter): Promise<AggregatedRow[]> => apiRequest(`/api/work${buildWorkQuery(filter)}`),
  createWork: (repoId: string, input: CreateInput): Promise<AggregatedRow> => {
    if (!input.files.length) {
      return apiRequest(`/api/repos/${encodeURIComponent(repoId)}/work`, {
        method: "POST",
        body: JSON.stringify({ title: input.title, tags: input.tags, priority: input.priority, description: input.description })
      });
    }
    const form = new FormData();
    form.append("title", input.title);
    form.append("tags", input.tags.join(","));
    form.append("priority", input.priority);
    form.append("description", input.description);
    for (const file of input.files) form.append("file", file.blob, file.name);
    return apiRequest(`/api/repos/${encodeURIComponent(repoId)}/work`, { method: "POST", body: form });
  }
};

// ---------------------------------------------------------------------------
// Pure rendering
// ---------------------------------------------------------------------------

function el<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  attrs: Record<string, string> = {},
  children: (Node | string)[] = []
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, value);
  for (const child of children) node.append(child);
  return node;
}

function renderTags(tags: readonly string[] | undefined): HTMLElement {
  return el("span", {}, (tags ?? []).map((tag) => el("span", { class: "tag" }, [tag])));
}

function statusPill(status: string | undefined): HTMLElement {
  if (!status) return el("span", {}, [""]);
  return el("span", { class: `status-pill status-${status}` }, [status]);
}

function renderErrorText(id: string, message: string | null): void {
  const node = document.getElementById(id);
  if (!node) return;
  if (!message) { node.hidden = true; node.textContent = ""; return; }
  node.hidden = false;
  node.textContent = message;
}

function renderReposTable(repos: readonly RepoEntry[]): void {
  const body = document.getElementById("repos-table-body");
  if (!(body instanceof HTMLTableSectionElement)) throw new Error("repos-table-body missing");
  body.replaceChildren(...repos.map((repo) => {
    const removeButton = el("button", { type: "button" }, ["Unregister"]);
    removeButton.addEventListener("click", () => void runUnregister(repo));
    return el("tr", {}, [
      el("td", {}, [repo.id]),
      el("td", {}, [repo.name]),
      el("td", {}, [repo.path]),
      el("td", {}, [removeButton])
    ]);
  }));
}

function renderRepoOptions(repos: readonly RepoEntry[]): void {
  const createSelect = document.getElementById("create-repo");
  const filterSelect = document.getElementById("filter-repo");
  if (createSelect instanceof HTMLSelectElement) {
    const current = createSelect.value;
    createSelect.replaceChildren(...repos.map((repo) => el("option", { value: repo.id }, [repo.name])));
    if (repos.some((repo) => repo.id === current)) createSelect.value = current;
  }
  if (filterSelect instanceof HTMLSelectElement) {
    const current = filterSelect.value;
    filterSelect.replaceChildren(
      el("option", { value: "" }, ["all"]),
      ...repos.map((repo) => el("option", { value: repo.id }, [repo.name]))
    );
    filterSelect.value = repos.some((repo) => repo.id === current) ? current : "";
  }
}

function renderWorkRow(row: AggregatedRow): HTMLTableRowElement {
  if (row.error) {
    return el("tr", { class: "error-row" }, [
      el("td", {}, [row.repoName]),
      el("td", { colspan: "4" }, [row.error])
    ]);
  }
  return el("tr", {}, [
    el("td", {}, [row.repoName]),
    el("td", {}, [row.id ?? ""]),
    el("td", {}, [row.title ?? ""]),
    el("td", {}, [statusPill(row.status)]),
    el("td", {}, [renderTags(row.tags)]),
    el("td", {}, [row.priority ?? ""])
  ]);
}

function renderWorkTable(rows: readonly AggregatedRow[]): void {
  const body = document.getElementById("work-table-body");
  if (!(body instanceof HTMLTableSectionElement)) throw new Error("work-table-body missing");
  body.replaceChildren(...rows.map(renderWorkRow));
}

function render(current: State): void {
  renderReposTable(current.repos);
  renderRepoOptions(current.repos);
  renderWorkTable(current.rows);
  renderErrorText("repos-error", current.reposError);
  renderErrorText("create-error", current.createError);
  renderErrorText("error", current.listError);
}

// ---------------------------------------------------------------------------
// File-row helpers (identical pattern to web/app.ts)
// ---------------------------------------------------------------------------

function addFileRow(container: HTMLElement): void {
  container.append(el("div", { class: "file-row" }, [
    el("input", { type: "file", "data-role": "file-input" }),
    el("input", { type: "text", placeholder: "name (optional)", "data-role": "file-name" })
  ]));
}

function resetFileRows(container: HTMLElement): void {
  container.replaceChildren();
  addFileRow(container);
}

function fileRowsFrom(container: HTMLElement): { blob: File; name: string }[] {
  const files: { blob: File; name: string }[] = [];
  for (const row of Array.from(container.children)) {
    const fileInput = row.querySelector('[data-role="file-input"]');
    const nameInput = row.querySelector('[data-role="file-name"]');
    const blob = fileInput instanceof HTMLInputElement ? fileInput.files?.[0] : undefined;
    if (!blob) continue;
    const name = nameInput instanceof HTMLInputElement ? nameInput.value.trim() : "";
    files.push({ blob, name: name || blob.name });
  }
  return files;
}

function parseTags(raw: string): string[] {
  return raw.split(",").map((tag) => tag.trim()).filter(Boolean);
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

async function refreshRepos(): Promise<void> {
  try {
    const repos = await api.repos();
    setState({ repos, reposError: null });
  } catch (error) {
    setState({ reposError: (error as Error).message });
  }
}

async function refreshWork(): Promise<void> {
  try {
    const rows = await api.work(state.filter);
    setState({ rows, listError: null });
  } catch (error) {
    setState({ listError: (error as Error).message });
  }
}

async function runUnregister(repo: RepoEntry): Promise<void> {
  if (!window.confirm(`Unregister ${repo.name} (${repo.path})? This only removes it from the hub -- the repository itself is unaffected.`)) return;
  try {
    await api.unregister(repo.id);
    await refreshRepos();
    await refreshWork();
  } catch (error) {
    setState({ reposError: (error as Error).message });
  }
}

function wireRegisterForm(): void {
  const form = document.getElementById("register-form");
  const pathInput = document.getElementById("register-path");
  const nameInput = document.getElementById("register-name");
  if (!(form instanceof HTMLFormElement)) throw new Error("register-form missing");

  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const repoPath = pathInput instanceof HTMLInputElement ? pathInput.value.trim() : "";
    const name = nameInput instanceof HTMLInputElement ? nameInput.value.trim() : "";
    if (!repoPath) return;
    void (async () => {
      try {
        await api.register(repoPath, name);
        setState({ reposError: null });
        form.reset();
        await refreshRepos();
        await refreshWork();
      } catch (error) {
        setState({ reposError: (error as Error).message });
      }
    })();
  });
}

function wireCreateForm(): void {
  const form = document.getElementById("create-form");
  const repoSelect = document.getElementById("create-repo");
  const titleInput = document.getElementById("create-title");
  const tagsInput = document.getElementById("create-tags");
  const priorityInput = document.getElementById("create-priority");
  const descriptionInput = document.getElementById("create-description");
  const filesContainer = document.getElementById("create-files");
  const addFileButton = document.getElementById("create-add-file");
  if (!(form instanceof HTMLFormElement)) throw new Error("create-form missing");
  if (!(filesContainer instanceof HTMLElement)) throw new Error("create-files missing");

  resetFileRows(filesContainer);
  addFileButton?.addEventListener("click", () => addFileRow(filesContainer));

  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const repoId = repoSelect instanceof HTMLSelectElement ? repoSelect.value : "";
    const title = titleInput instanceof HTMLInputElement ? titleInput.value.trim() : "";
    const tags = parseTags(tagsInput instanceof HTMLInputElement ? tagsInput.value : "");
    const priority = priorityInput instanceof HTMLSelectElement ? priorityInput.value : "medium";
    const description = descriptionInput instanceof HTMLTextAreaElement ? descriptionInput.value.trim() : "";
    const files = fileRowsFrom(filesContainer);
    if (!repoId || !title) return;

    void (async () => {
      try {
        await api.createWork(repoId, { title, tags, priority, description, files });
        setState({ createError: null });
        form.reset();
        resetFileRows(filesContainer);
        await refreshWork();
      } catch (error) {
        setState({ createError: (error as Error).message });
      }
    })();
  });
}

function wireFilters(): void {
  const repoSelect = document.getElementById("filter-repo");
  const tagInput = document.getElementById("filter-tag");
  const statusSelect = document.getElementById("filter-status");
  const clearButton = document.getElementById("filter-clear");

  repoSelect?.addEventListener("change", () => {
    const value = repoSelect instanceof HTMLSelectElement ? repoSelect.value : "";
    setState({ filter: { ...state.filter, repo: value } });
    void refreshWork();
  });

  tagInput?.addEventListener("input", () => {
    const value = tagInput instanceof HTMLInputElement ? tagInput.value : "";
    setState({ filter: { ...state.filter, tag: value } });
    void refreshWork();
  });

  statusSelect?.addEventListener("change", () => {
    const value = statusSelect instanceof HTMLSelectElement ? statusSelect.value : "";
    setState({ filter: { ...state.filter, status: value } });
    void refreshWork();
  });

  clearButton?.addEventListener("click", () => {
    if (repoSelect instanceof HTMLSelectElement) repoSelect.value = "";
    if (tagInput instanceof HTMLInputElement) tagInput.value = "";
    if (statusSelect instanceof HTMLSelectElement) statusSelect.value = "";
    setState({ filter: { repo: "", tag: "", status: "" } });
    void refreshWork();
  });
}

function main(): void {
  wireRegisterForm();
  wireCreateForm();
  wireFilters();
  render(state);
  void refreshRepos();
  void refreshWork();
}

main();
