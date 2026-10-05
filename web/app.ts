// Browser kernel for the work-backlog page: the only code here that touches
// the DOM or the network. Everything the page decides -- its state and
// transitions, which requests to make, which actions a row offers, what the
// detail panel shows, how typed input becomes a submission -- lives in the
// engine (./engine/backlog.ts) as pure functions. This file performs the
// requests the engine describes, reads raw input out of the DOM, and renders
// what the engine projects (Limen's kernel side; see limen.config.json).
//
// The flow is always: DOM event -> engine request -> fetch -> server ->
// engine transition -> render(state) -> DOM. Nothing writes to `state` except
// `dispatch`, and nothing writes to the DOM except `render` and the dialog
// helpers. Validation of work-item transitions lives only on the server,
// which itself only calls into ros_cli.mjs.

import {
  addInput,
  completionInput,
  detailView,
  filtersCleared,
  initialState,
  repositoryLabel,
  requestFailed,
  requests,
  responseError,
  rowActions,
  rowsLoaded,
  rowToggled,
  statusFilterChanged,
  tagFilterChanged,
  updateDefaults,
  updateInput,
  uploadName,
  type AttachmentView,
  type CompletionInput,
  type RepositoryStatus,
  type Request,
  type RowActionKind,
  type State,
  type UpdateInput,
  type WorkRow
} from "./engine/backlog.js";

let state: State = initialState;

function dispatch(transition: (current: State) => State): void {
  state = transition(state);
  render(state);
}

// ---------------------------------------------------------------------------
// Effects: the only functions in this file that talk to the network.
// ---------------------------------------------------------------------------

type FileUpload = { blob: File; name: string };

async function perform<T>(request: Request, form?: FormData): Promise<T> {
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

function uploadAttachments(id: string, files: readonly FileUpload[]): Promise<WorkRow> {
  const form = new FormData();
  for (const file of files) form.append("file", file.blob, file.name);
  return perform(requests.uploadAttachments(id), form);
}

// ---------------------------------------------------------------------------
// Rendering: (projection) -> DOM nodes.
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

function renderTags(tags: readonly string[]): HTMLElement {
  return el("span", {}, tags.map((tag) => el("span", { class: "tag" }, [tag])));
}

function statusPill(status: string): HTMLElement {
  return el("span", { class: `status-pill status-${status}` }, [status]);
}

function actionHandler(kind: RowActionKind, row: WorkRow): () => void {
  switch (kind) {
    case "show": return () => dispatch((current) => rowToggled(current, row.id));
    case "ready": return () => void runRequest(requests.ready(row.id));
    case "resume": return () => void runRequest(requests.resume(row.id));
    case "block": return () => void runBlock(row.id);
    case "abandon": return () => void runAbandon(row.id);
    case "start": return () => void runStart(row.id);
    case "complete": return () => void runComplete(row.id);
    case "edit": return () => void runUpdate(row);
    case "attach": return () => void runAttach(row.id);
  }
}

function renderRow(row: WorkRow): HTMLTableRowElement {
  const actionsCell = el("td", { class: "row-actions" });
  for (const action of rowActions(row)) {
    const button = el("button", { type: "button" }, [action.label]);
    button.addEventListener("click", actionHandler(action.kind, row));
    actionsCell.append(button);
  }
  return el("tr", { "data-id": row.id }, [
    el("td", {}, [row.id]),
    el("td", {}, [row.title]),
    el("td", {}, [statusPill(row.status)]),
    el("td", {}, [renderTags(row.tags)]),
    el("td", {}, [row.priority ?? ""]),
    actionsCell
  ]);
}

function renderTable(rows: readonly WorkRow[]): void {
  const body = document.getElementById("work-table-body");
  if (!(body instanceof HTMLTableSectionElement)) throw new Error("work-table-body missing");
  body.replaceChildren(...rows.map(renderRow));
}

function renderError(message: string | null): void {
  const node = document.getElementById("error");
  if (!node) return;
  if (!message) { node.hidden = true; node.textContent = ""; return; }
  node.hidden = false;
  node.textContent = message;
}

function renderAttachments(attachments: readonly AttachmentView[]): HTMLElement {
  if (!attachments.length) return el("p", { class: "muted" }, ["No attachments."]);
  return el("ul", { class: "attachment-list" }, attachments.map((attachment) =>
    el("li", {}, [el("a", { href: attachment.href, download: "" }, [attachment.name]), attachment.sizeLabel])
  ));
}

function renderDetail(current: State): void {
  const section = document.getElementById("detail");
  const summary = document.getElementById("detail-summary");
  const body = document.getElementById("detail-body");
  if (!section || !summary || !body) return;
  const view = detailView(current);
  if (!view) { section.hidden = true; summary.replaceChildren(); body.textContent = ""; return; }

  section.hidden = false;
  summary.replaceChildren(el("p", {}, [view.description]), renderAttachments(view.attachments));
  body.textContent = view.raw;
}

function render(current: State): void {
  renderTable(current.rows);
  renderError(current.error);
  renderDetail(current);
}

// ---------------------------------------------------------------------------
// Dialog helpers: native <dialog> + <form method="dialog"> already closes
// itself and sets `returnValue` to the clicked button's `value` attribute.
// These functions only open the dialog, reset its fields, and read back the
// raw values the user entered; the engine turns them into a submission.
// ---------------------------------------------------------------------------

function dialogEl(id: string): HTMLDialogElement {
  const node = document.getElementById(id);
  if (!(node instanceof HTMLDialogElement)) throw new Error(`missing dialog #${id}`);
  return node;
}

function waitForClose(dialog: HTMLDialogElement): Promise<string> {
  return new Promise((resolve) => {
    dialog.addEventListener("close", () => resolve(dialog.returnValue), { once: true });
  });
}

function textOf(node: Element | null): string {
  return node instanceof HTMLInputElement ? node.value : "";
}

async function promptReason(title: string): Promise<string | null> {
  const dialog = dialogEl("reason-dialog");
  const titleNode = document.getElementById("reason-dialog-title");
  const input = document.getElementById("reason-dialog-input");
  if (titleNode) titleNode.textContent = title;
  if (input instanceof HTMLTextAreaElement) input.value = "";
  dialog.showModal();
  const result = await waitForClose(dialog);
  if (result !== "confirm") return null;
  return input instanceof HTMLTextAreaElement ? input.value : "";
}

async function promptStartType(): Promise<string | null> {
  const dialog = dialogEl("start-dialog");
  const select = document.getElementById("start-dialog-type");
  dialog.showModal();
  const result = await waitForClose(dialog);
  if (result !== "confirm") return null;
  return select instanceof HTMLSelectElement ? select.value : "feature";
}

function addEvidenceRow(container: HTMLElement): void {
  container.append(el("div", { class: "evidence-row" }, [
    el("input", { placeholder: "type (e.g. implementation)", "data-role": "evidence-type" }),
    el("input", { placeholder: "path", "data-role": "evidence-path" })
  ]));
}

async function promptCompletion(): Promise<CompletionInput | null> {
  const dialog = dialogEl("complete-dialog");
  const rows = document.getElementById("complete-evidence-rows");
  const conclusionInput = document.getElementById("complete-conclusion");
  const addButton = document.getElementById("complete-add-row");
  if (!rows) throw new Error("missing complete-evidence-rows");

  rows.replaceChildren();
  addEvidenceRow(rows);
  addEvidenceRow(rows);
  if (conclusionInput instanceof HTMLInputElement) conclusionInput.value = "";

  const onAdd = (): void => addEvidenceRow(rows);
  addButton?.addEventListener("click", onAdd);
  dialog.showModal();
  const result = await waitForClose(dialog);
  addButton?.removeEventListener("click", onAdd);
  if (result !== "confirm") return null;

  const entered = Array.from(rows.children).map((row) => ({
    type: textOf(row.querySelector('[data-role="evidence-type"]')),
    path: textOf(row.querySelector('[data-role="evidence-path"]'))
  }));
  return completionInput(entered, textOf(conclusionInput));
}

// A file's associated `name` is independent of what was actually selected on
// disk -- the optional text input next to each file picker overrides it, the
// same association the CLI's `--file PATH=NAME` expresses.
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

async function promptUpdate(row: WorkRow): Promise<UpdateInput | null> {
  const dialog = dialogEl("update-dialog");
  const titleInput = document.getElementById("update-dialog-title");
  const descriptionInput = document.getElementById("update-dialog-description");
  const tagsInput = document.getElementById("update-dialog-tags");
  const priorityInput = document.getElementById("update-dialog-priority");
  const defaults = updateDefaults(row);
  if (titleInput instanceof HTMLInputElement) titleInput.value = defaults.title;
  if (descriptionInput instanceof HTMLTextAreaElement) descriptionInput.value = defaults.description;
  if (tagsInput instanceof HTMLInputElement) tagsInput.value = defaults.tags;
  if (priorityInput instanceof HTMLSelectElement) priorityInput.value = defaults.priority;

  dialog.showModal();
  const result = await waitForClose(dialog);
  if (result !== "confirm") return null;
  return updateInput({
    title: titleInput instanceof HTMLInputElement ? titleInput.value : null,
    description: descriptionInput instanceof HTMLTextAreaElement ? descriptionInput.value : "",
    tags: textOf(tagsInput),
    priority: priorityInput instanceof HTMLSelectElement ? priorityInput.value : "medium"
  }, row);
}

async function promptAttachments(): Promise<FileUpload[] | null> {
  const dialog = dialogEl("attach-dialog");
  const rows = document.getElementById("attach-file-rows");
  const addButton = document.getElementById("attach-add-row");
  if (!(rows instanceof HTMLElement)) throw new Error("missing attach-file-rows");

  resetFileRows(rows);
  const onAdd = (): void => addFileRow(rows);
  addButton?.addEventListener("click", onAdd);
  dialog.showModal();
  const result = await waitForClose(dialog);
  addButton?.removeEventListener("click", onAdd);
  if (result !== "confirm") return null;
  return fileRowsFrom(rows);
}

// ---------------------------------------------------------------------------
// Commands: perform an engine request, then feed the outcome back through an
// engine transition.
// ---------------------------------------------------------------------------

async function refresh(): Promise<void> {
  try {
    const rows = await perform<WorkRow[]>(requests.list(state.filter));
    dispatch((current) => rowsLoaded(current, rows));
  } catch (error) {
    dispatch((current) => requestFailed(current, (error as Error).message));
  }
}

async function runWithRefresh(action: () => Promise<unknown>): Promise<void> {
  try {
    await action();
    await refresh();
  } catch (error) {
    dispatch((current) => requestFailed(current, (error as Error).message));
  }
}

function runRequest(request: Request): Promise<void> {
  return runWithRefresh(() => perform(request));
}

async function runBlock(id: string): Promise<void> {
  const reason = await promptReason("Reason for blocking");
  if (reason === null) return;
  await runRequest(requests.block(id, reason));
}

async function runAbandon(id: string): Promise<void> {
  const reason = await promptReason("Reason for abandoning");
  if (reason === null) return;
  await runRequest(requests.abandon(id, reason));
}

async function runStart(id: string): Promise<void> {
  const type = await promptStartType();
  if (type === null) return;
  await runRequest(requests.start(id, type));
}

async function runComplete(id: string): Promise<void> {
  const input = await promptCompletion();
  if (input === null) return;
  await runRequest(requests.complete(id, input));
}

async function runUpdate(row: WorkRow): Promise<void> {
  const input = await promptUpdate(row);
  if (input === null) return;
  await runRequest(requests.update(row.id, input));
}

async function runAttach(id: string): Promise<void> {
  const files = await promptAttachments();
  if (files === null || !files.length) return;
  await runWithRefresh(() => uploadAttachments(id, files));
}

function wireAddForm(): void {
  const form = document.getElementById("add-form");
  const titleInput = document.getElementById("add-title");
  const descriptionInput = document.getElementById("add-description");
  const tagsInput = document.getElementById("add-tags");
  const priorityInput = document.getElementById("add-priority");
  const filesContainer = document.getElementById("add-files");
  const addFileButton = document.getElementById("add-add-file");
  if (!(form instanceof HTMLFormElement)) throw new Error("add-form missing");
  if (!(filesContainer instanceof HTMLElement)) throw new Error("add-files missing");

  resetFileRows(filesContainer);
  addFileButton?.addEventListener("click", () => addFileRow(filesContainer));

  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const input = addInput({
      title: textOf(titleInput),
      description: descriptionInput instanceof HTMLTextAreaElement ? descriptionInput.value : "",
      tags: textOf(tagsInput),
      priority: priorityInput instanceof HTMLSelectElement ? priorityInput.value : "medium"
    });
    const files = fileRowsFrom(filesContainer);
    if (!input) return;

    void runWithRefresh(async () => {
      const created = await perform<WorkRow>(requests.add(input));
      if (files.length) await uploadAttachments(created.id, files);
    }).then(() => {
      form.reset();
      resetFileRows(filesContainer);
    });
  });
}

function wireFilters(): void {
  const tagInput = document.getElementById("filter-tag");
  const statusSelect = document.getElementById("filter-status");
  const clearButton = document.getElementById("filter-clear");

  tagInput?.addEventListener("input", () => {
    const value = textOf(tagInput);
    dispatch((current) => tagFilterChanged(current, value));
    void refresh();
  });

  statusSelect?.addEventListener("change", () => {
    const value = statusSelect instanceof HTMLSelectElement ? statusSelect.value : "";
    dispatch((current) => statusFilterChanged(current, value));
    void refresh();
  });

  clearButton?.addEventListener("click", () => {
    if (tagInput instanceof HTMLInputElement) tagInput.value = "";
    if (statusSelect instanceof HTMLSelectElement) statusSelect.value = "";
    dispatch(filtersCleared);
    void refresh();
  });
}

async function showRepositoryLabel(): Promise<void> {
  const node = document.getElementById("repository-label");
  if (!node) return;
  try {
    node.textContent = repositoryLabel(await perform<RepositoryStatus>(requests.status()));
  } catch {
    node.textContent = "";
  }
}

function main(): void {
  wireAddForm();
  wireFilters();
  render(state);
  void refresh();
  void showRepositoryLabel();
}

main();
