// Limen kernel side for the accounting application: starts the shared
// kernel (web-kernel/limen-wasm.js) against the application engine. Nothing else.
import { startApp } from "../web-kernel/limen-wasm.js";

await startApp();
