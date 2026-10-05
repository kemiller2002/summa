// Limen kernel side for the work-backlog page: starts the shared kernel
// (web-kernel/limen-wasm.js) against this page's engine export. Nothing else.
import { startPage } from "../web-kernel/limen-wasm.js";

await startPage("DispatchBacklog");
