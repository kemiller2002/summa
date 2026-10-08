// Limen kernel side - browser interop only. Shared by web/, web-hub/ and app/.
//
// Loads the published .NET WebAssembly runtime, hands each Limen message, as
// JSON, to one page's [JSExport] in the Summa.Wasm shim, and starts Limen's
// BrowserKernel with the two optional packs the pages need: user-picked files
// (`limen.files`) and multipart uploads of them (`limen.transfer`). It never
// inspects a message: every application decision is made by the F# engine on
// the far side of the transport (src/Summa.Web.Engine, src/Summa.Web.Application).
//
// Folio's print components are registered here too, so the print surface on
// each page upgrades; they are inert light-DOM elements with no behaviour.
import "../node_modules/@echelon-foundry/print-components/src/components/register.js";
import { BrowserKernel } from "../node_modules/@echelon-foundry/limen/dist/kernel/browser-kernel.js";
import { filesCapability } from "../node_modules/@echelon-foundry/limen/dist/capabilities/files/index.js";
import { transferCapability } from "../node_modules/@echelon-foundry/limen/dist/capabilities/transfer/index.js";
import { storeCapability } from "../node_modules/@echelon-foundry/limen/dist/capabilities/store/index.js";
import { printCapability } from "./print.js";

// Relative to this module, so it resolves the same in the checkout and over
// HTTP (tools/web_static.mjs serves these directories at the same paths).
const FRAMEWORK = "../build/wasm/wwwroot/_framework";

class WasmEngineTransport {
  #dispatch = null;
  #exportName;

  constructor(exportName) {
    this.#exportName = exportName;
  }

  async start() {
    const { dotnet } = await import(`${FRAMEWORK}/dotnet.js`);
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    const dispatch = exports.SummaWasm?.[this.#exportName];
    if (typeof dispatch !== "function") {
      throw new Error(`SummaWasm.${this.#exportName} export not found; run \`npm run build:wasm\` to republish the engine.`);
    }
    this.#dispatch = dispatch;
  }

  async dispatch(message) {
    if (this.#dispatch === null) throw new Error("dispatch() called before start()");
    return JSON.parse(this.#dispatch(JSON.stringify(message)));
  }
}

// Limen reports a bridge failure to its diagnostics sink rather than
// throwing, so the default no-op sink would make a broken engine look like an
// empty page. Failures are loud; everything else is debug output.
const diagnostics = {
  report(event) {
    if (event.kind === "BridgeError") {
      console.error(`[limen] bridge error during ${event.phase}: ${event.detail}`);
    } else if (event.kind === "Handshake" && event.verdict.kind === "Incompatible") {
      console.error(`[limen] engine and kernel are incompatible: ${JSON.stringify(event.verdict.reason)}`);
    } else {
      console.debug("[limen]", event.kind);
    }
  }
};

// The packs the backlog and hub pages select: user-picked files and their uploads.
const filePacks = () => {
  const files = filesCapability();
  return [files, transferCapability({ files })];
};

// Starts the kernel for one page. `exportName` is that page's SummaWasm export.
// The kernel's status is published on <html data-kernel> so a page (and its
// browser tests) can tell "running" from a failed start.
export async function startPage(exportName, capabilities = filePacks()) {
  const kernel = new BrowserKernel(new WasmEngineTransport(exportName), document, diagnostics, {
    capabilities,
    requireHandshake: true
  });
  await kernel.start();
  document.documentElement.dataset.kernel = kernel.status;
}

// The accounting application (app/): Limen's core effects (Storage keeps the
// books in this browser), Summa's print pack (./print.js), which opens the
// browser's print dialog for the invoice's Folio document, and the files and
// store packs, which read the PDF the person saved and keep it in this
// environment's artifact database.
export async function startApp() {
  await startPage("DispatchAccounting", [printCapability(), filesCapability(), storeCapability()]);
}
