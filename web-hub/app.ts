// Browser kernel for the project-administration hub page: the only code here
// that touches the DOM or the network. The page's state, transitions, request
// shapes and input decisions live in the engine (./engine/hub.ts) as pure
// functions; this file performs the requests the engine describes, reads raw
// input out of the DOM, and renders state (Limen's kernel side; see
// limen.config.json). Same discipline as web/app.ts: one state value, one
// dispatch that re-renders, no two-way binding.

import {
  createFailed,
  createInput,
  created,
  filtersCleared,
  initialState,
  registered,
  registerInput,
  repoFilterChanged,
  reposFailed,
  reposLoaded,
  requests,
  responseError,
  retainedSelection,
  statusFilterChanged,
  tagFilterChanged,
  unregisterConfirmation,
  uploadName,
  workFailed,
  workLoaded,
  type AggregatedRow,
  type RepoEntry,
  type Request,
  type State
} from "./engine/hub.js";

let state: State = initialState;

function dispatch(transition: (current: State) => State): void {
  state = transition(state);
  render(state);
}

// ---------------------------------------------------------------------------
// Effects
// ---------------------------------------------------------------------------

type FileUpload = { blob: File; name: string };

async function perform<T>(request: Request, files: readonly FileUpload[] = []): Promise<T> {
  let form: FormData | undefined;
  if (request.formFields) {
    form = new FormData();
    for (const [key, value] of request.formFields) form.append(key, value);
    for (const file of files) form.append("file", file.blob, file.name);
  }
  const body = form ?? (request.json === undefined ? undefined : JSON.stringify(request.json));
  const response = await fetch(request.path, {
    method: request.method,
    ...(body === undefined ? {} : { body }),
    headers: form ? {} : { "Content-Type": "application/json" }
  });
  const parsed: unknown = await response.json();
  if (!response.ok) throw new Error(responseError(response.status, parsed));
  return parsed as T;
}

// ---------------------------------------------------------------------------
// Rendering
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
    const kept = retainedSelection(repos, createSelect.value);
    createSelect.replaceChildren(...repos.map((repo) => el("option", { value: repo.id }, [repo.name])));
    if (kept !== null) createSelect.value = kept;
  }
  if (filterSelect instanceof HTMLSelectElement) {
    const kept = retainedSelection(repos, filterSelect.value);
    filterSelect.replaceChildren(
      el("option", { value: "" }, ["all"]),
      ...repos.map((repo) => el("option", { value: repo.id }, [repo.name]))
    );
    filterSelect.value = kept ?? "";
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

function textOf(node: Element | null): string {
  return node instanceof HTMLInputElement ? node.value : "";
}

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

function fileRowsFrom(container: HTMLElement): FileUpload[] {
  const files: FileUpload[] = [];
  for (const row of Array.from(container.children)) {
    const fileInput = row.querySelector('[data-role="file-input"]');
    const blob = fileInput instanceof HTMLInputElement ? fileInput.files?.[0] : undefined;
    if (!blob) continue;
    files.push({ blob, name: uploadName(textOf(row.querySelector('[data-role="file-name"]')), blob.name) });
  }
  return files;
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

async function refreshRepos(): Promise<void> {
  try {
    const repos = await perform<RepoEntry[]>(requests.repos());
    dispatch((current) => reposLoaded(current, repos));
  } catch (error) {
    dispatch((current) => reposFailed(current, (error as Error).message));
  }
}

async function refreshWork(): Promise<void> {
  try {
    const rows = await perform<AggregatedRow[]>(requests.work(state.filter));
    dispatch((current) => workLoaded(current, rows));
  } catch (error) {
    dispatch((current) => workFailed(current, (error as Error).message));
  }
}

async function runUnregister(repo: RepoEntry): Promise<void> {
  if (!window.confirm(unregisterConfirmation(repo))) return;
  try {
    await perform(requests.unregister(repo.id));
    await refreshRepos();
    await refreshWork();
  } catch (error) {
    dispatch((current) => reposFailed(current, (error as Error).message));
  }
}

function wireRegisterForm(): void {
  const form = document.getElementById("register-form");
  const pathInput = document.getElementById("register-path");
  const nameInput = document.getElementById("register-name");
  if (!(form instanceof HTMLFormElement)) throw new Error("register-form missing");

  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const input = registerInput({ path: textOf(pathInput), name: textOf(nameInput) });
    if (!input) return;
    void (async () => {
      try {
        await perform(requests.register(input.path, input.name));
        dispatch(registered);
        form.reset();
        await refreshRepos();
        await refreshWork();
      } catch (error) {
        dispatch((current) => reposFailed(current, (error as Error).message));
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
    const input = createInput({
      repoId: repoSelect instanceof HTMLSelectElement ? repoSelect.value : "",
      title: textOf(titleInput),
      tags: textOf(tagsInput),
      priority: priorityInput instanceof HTMLSelectElement ? priorityInput.value : "medium",
      description: descriptionInput instanceof HTMLTextAreaElement ? descriptionInput.value : ""
    });
    const files = fileRowsFrom(filesContainer);
    if (!input) return;

    void (async () => {
      try {
        await perform(requests.createWork(input, files.length > 0), files);
        dispatch(created);
        form.reset();
        resetFileRows(filesContainer);
        await refreshWork();
      } catch (error) {
        dispatch((current) => createFailed(current, (error as Error).message));
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
    dispatch((current) => repoFilterChanged(current, value));
    void refreshWork();
  });

  tagInput?.addEventListener("input", () => {
    const value = textOf(tagInput);
    dispatch((current) => tagFilterChanged(current, value));
    void refreshWork();
  });

  statusSelect?.addEventListener("change", () => {
    const value = statusSelect instanceof HTMLSelectElement ? statusSelect.value : "";
    dispatch((current) => statusFilterChanged(current, value));
    void refreshWork();
  });

  clearButton?.addEventListener("click", () => {
    if (repoSelect instanceof HTMLSelectElement) repoSelect.value = "";
    if (tagInput instanceof HTMLInputElement) tagInput.value = "";
    if (statusSelect instanceof HTMLSelectElement) statusSelect.value = "";
    dispatch(filtersCleared);
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
