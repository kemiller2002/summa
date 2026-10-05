// Limen kernel side for the project-administration hub page: starts the
// shared kernel (web-kernel/limen-wasm.js) against this page's engine export.
import { startPage } from "../web-kernel/limen-wasm.js";

await startPage("DispatchHub");
