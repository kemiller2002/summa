// UI-LAYOUT-INTERACTION@1.0.0 conformance for the accounting application
// (app/), summa#90 / WI-0053. Each test cites the rules it observes. Peers are
// declared here, by accessible role and name; nothing is inferred from CSS.
// Every title carries @ui-conformance so `npm run ui:conformance` selects it.
import { test, expect } from "./support.js";
import { STATUS, expectActionOutcome, measureAlignment, formatReport, settleFontsAndFrames } from "./ui-conformance-helpers.js";
import { WIDTHS, expectFieldRows, expectNoHorizontalOverflow, setWidth } from "./ui-conformance-support.js";

const field = (role, name) => ({ role, name });
const ready = (page) => expect(page.getByRole("main").getByRole("heading", { level: 1 })).toBeVisible();

async function open(page, origin, hash) {
  await page.goto(`${origin}/${hash}`);
  await ready(page);
}

// A customer whose name has no break opportunity: the long-label case.
const LONG_NAME = "Supercalifragilistic-International-Consulting-Holdings-Incorporated-Worldwide";

async function addCustomer(page, name, address = "1 Main Street") {
  await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Customers", exact: true }).click();
  await page.getByRole("textbox", { name: "Name", exact: true }).fill(name);
  await page.getByRole("textbox", { name: "Billing address" }).fill(address);
  await page.getByRole("button", { name: "Add customer" }).click();
  await expect(page.getByRole("status").filter({ hasText: `Customer ${name} added.` })).toBeVisible();
}

async function issueInvoice(page, customerLabel, description, rate) {
  await page.getByRole("link", { name: "New invoice", exact: true }).click();
  await page.getByRole("combobox", { name: "Customer", exact: true }).selectOption({ label: customerLabel });
  await page.getByRole("textbox", { name: "Line 1 description" }).fill(description);
  await page.getByRole("textbox", { name: "Line 1 rate" }).fill(rate);
  await page.getByRole("button", { name: "Save and review" }).click();
  // Issuing asks first (WI-0044): the editor's button opens a dialog.
  await page.getByRole("button", { name: "Issue invoice…" }).click();
  await page.getByRole("dialog", { name: "Issue this invoice?" }).getByRole("button", { name: "Issue invoice", exact: true }).click();
  await expect(page.getByRole("heading", { level: 1, name: /^Invoice INV-/ })).toBeVisible();
}

const EDITOR_HEAD = [field("combobox", "Customer"), field("textbox", "Purchase order")];
const TAX = [field("textbox", "Tax code"), field("textbox", "Tax amount"), field("textbox", "Rate (%)"), field("combobox", "Liability account")];
const LINE = [field("textbox", "Line 1 description"), field("textbox", "Line 1 quantity"), field("textbox", "Line 1 rate")];
const EDITOR_ACTIONS = [field("button", "Save and review"), field("button", "Issue invoice…")];
const CUSTOMER = [field("textbox", "Name"), field("textbox", "Billing name"), field("textbox", "Billing email"), field("textbox", "Payment terms (days)")];

test("@ui-conformance invoice editor: declared peer fields share a top edge, or stack in order (ULI-L01, ULI-L09, ULI-L10)", async ({ app: { page, origin } }) => {
  await open(page, origin, "#/invoices/new");
  // Customer carries help text and Purchase order does not; their controls
  // must still line up (the baseline defect: 24 CSS px apart at 1280).
  await expectFieldRows(page, { id: "editor head", peers: EDITOR_HEAD, rows: { 768: [EDITOR_HEAD], 1280: [EDITOR_HEAD] } });
  await expectFieldRows(page, { id: "tax", peers: TAX, rows: { 768: [TAX.slice(0, 2), TAX.slice(2)], 1280: [TAX] } });
  // The line row is a table row at 768 and up; below 40rem it stacks.
  await expectFieldRows(page, { id: "line 1", peers: LINE, rows: { 768: [LINE], 1280: [LINE] } });
  await expectFieldRows(page, { id: "editor actions", peers: EDITOR_ACTIONS, rows: { 768: [EDITOR_ACTIONS], 1280: [EDITOR_ACTIONS] } });
});

test("@ui-conformance add-customer form: declared peer fields share a top edge, or stack in order (ULI-L01, ULI-L09)", async ({ app: { page, origin } }) => {
  await open(page, origin, "#/customers");
  // Billing name and Payment terms carry help text; Name and email do not.
  await expectFieldRows(page, {
    id: "customer",
    peers: CUSTOMER,
    rows: { 768: [CUSTOMER.slice(0, 2), CUSTOMER.slice(2)], 1280: [CUSTOMER] }
  });
});

test("@ui-conformance invoice line inputs are visible, usable controls at every width (ULI-A06, ULI-L05)", async ({ app: { page, origin } }) => {
  await open(page, origin, "#/invoices/new");
  for (const width of WIDTHS) {
    await setWidth(page, width);
    await settleFontsAndFrames(page);
    for (const { role, name } of LINE) {
      const input = page.getByRole(role, { name });
      await expect(input).toBeVisible();
      // The baseline defect: each input sat inside a visually hidden label,
      // clipped away, so only automation could fill it. Its box was full
      // size; nothing of it was painted or could be pointed at. The input
      // itself must be what receives a pointer at its own centre.
      const box = await input.boundingBox();
      expect(box.width, `${name} width @${width}`).toBeGreaterThanOrEqual(64);
      expect(box.height, `${name} height @${width}`).toBeGreaterThanOrEqual(44);
      await input.scrollIntoViewIfNeeded();
      expect(await input.evaluate((element) => {
        const rect = element.getBoundingClientRect();
        return document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2) === element;
      }), `${name} is hit-testable @${width}`).toBe(true);
    }
    // No header or button is split mid-word (the baseline defect at 768:
    // "Re/mo/ve" and "Taxa/ble").
    const remove = page.getByRole("button", { name: "Remove" });
    const lines = await remove.evaluate((button) => Math.round(button.getBoundingClientRect().height / parseFloat(getComputedStyle(button).lineHeight)));
    expect(lines, `Remove wraps @${width}`).toBeLessThanOrEqual(2);
    expect(await remove.evaluate((button) => button.scrollWidth <= button.clientWidth)).toBe(true);
  }
});

// Forma 0.6.1 gives text inputs a content-box height of 50 CSS px and native
// selects a border-box height of 46.4: equivalent controls in one row differ
// by 3.6 px. Forma owns control geometry (ULI-L02/L03); Summa does not patch
// it locally. This test records the defect and starts failing (as a reminder
// to drop the expectation) when the pinned Forma equalizes them.
test("@ui-conformance equivalent controls in a declared row have equal heights (ULI-L02, upstream Forma)", async ({ app: { page, origin } }) => {
  test.info().annotations.push({ type: "upstream", description: "Forma 0.6.1: input 50px vs select 46.4px (box-sizing not normalized in :where(input, textarea, select))" });
  test.fail(true, "Known Forma-owned ULI-L02 defect; see annotation.");
  await open(page, origin, "#/invoices/new");
  await setWidth(page, 1280);
  // Tops align (ULI-L01, asserted above); equal heights means equal bottoms.
  const report = await measureAlignment(page, { ruleId: "ULI-L02", id: "tax row heights", peers: TAX, edge: "bottom" });
  expect(report.status, formatReport(report)).toBe(STATUS.PASS);
});

test("@ui-conformance a disabled Issue says why, and the invoice workflow has observable outcomes (ULI-A02, ULI-A04, ULI-A10)", async ({ app: { page } }) => {
  await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Customers", exact: true }).click();
  await page.getByRole("textbox", { name: "Name", exact: true }).fill("Acme");
  await page.getByRole("textbox", { name: "Billing address" }).fill("1 Main Street");
  // Before: no customer Acme is listed. After: its link is.
  await expectActionOutcome(page, {
    ruleId: "ULI-A02",
    action: field("button", "Add customer"),
    outcome: { kind: "visible", target: field("link", "Acme"), expected: true }
  });

  await page.getByRole("link", { name: "New invoice", exact: true }).click();
  const issue = page.getByRole("button", { name: "Issue invoice…" });
  await expect(issue).toBeDisabled();
  await expect(issue).toHaveAccessibleDescription("Issue is available after Save and review has checked this invoice.");
  // With no customer chosen the field says what choosing one shows, not
  // "Terms: . Tax:" (ULI-L08).
  await expect(page.getByRole("combobox", { name: "Customer", exact: true })).toHaveAccessibleDescription("Choose a customer to see their payment terms and tax status.");

  await page.getByRole("combobox", { name: "Customer", exact: true }).selectOption({ label: "Acme" });
  await expect(page.getByRole("combobox", { name: "Customer", exact: true })).toHaveAccessibleDescription(/^Terms: .+\. Tax: .+/);
  await page.getByRole("textbox", { name: "Line 1 description" }).fill("Assessment");
  await page.getByRole("textbox", { name: "Line 1 rate" }).fill("1000");
  await expectActionOutcome(page, {
    ruleId: "ULI-A02",
    action: field("button", "Save and review"),
    outcome: { kind: "visible", target: field("heading", /^Preview: invoice/), expected: true }
  });
  await expect(issue).toBeEnabled();
  // Issue asks first; Not yet leaves the draft where it is.
  const draftUrl = page.url();
  await expectActionOutcome(page, {
    ruleId: "ULI-A02",
    action: field("button", "Issue invoice…"),
    outcome: { kind: "visible", target: field("dialog", "Issue this invoice?"), expected: true }
  });
  await page.getByRole("button", { name: "Not yet" }).click();
  await expect(page.getByRole("dialog", { name: "Issue this invoice?" })).toHaveCount(0);
  await expect(page).toHaveURL(draftUrl);
  await issue.click();
  await expectActionOutcome(page, {
    ruleId: "ULI-A02",
    action: field("button", "Issue invoice"),
    outcome: { kind: "url", expected: /#\/invoices\/INV-0001$/ }
  });
  await expect(page.getByRole("heading", { level: 1, name: "Invoice INV-2026-0001" })).toBeVisible();
  await expect(page.getByText("1,000.00 USD").first()).toBeVisible();
});

test("@ui-conformance leaving an invoice unmounts it: each route shows only its own view (ULI-L08, ULI-A02)", async ({ app: { page } }) => {
  await addCustomer(page, "Acme");
  await issueInvoice(page, "Acme", "Assessment", "300");
  const main = page.getByRole("main");
  for (const label of ["Customers", "Invoices", "Home"]) {
    await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: label, exact: true }).click();
    await expect(main.getByRole("heading", { level: 1 })).toHaveCount(1);
    // The baseline defect: the invoice stayed mounted below every later view.
    await expect(main.getByRole("heading", { name: /^Invoice INV-/ })).toHaveCount(0);
    await expect(main.getByRole("button", { name: "Print or save as PDF" })).toHaveCount(0);
  }
});

const NAVIGATION = [
  ["Home", /#\/$/, "Home"],
  ["Work", /#\/work$/, "Work"],
  ["Invoices", /#\/invoices$/, "Invoices"],
  ["Proposals", /#\/proposals$/, "Proposals"],
  ["Customers", /#\/customers$/, "Customers"],
  ["Engagements", /#\/engagements$/, "Engagements"],
  ["Payments", /#\/payments$/, "Payments"],
  ["Credit memos", /#\/credit-memos$/, "Credit memos"],
  ["Receivables", /#\/receivables$/, "Receivables"],
  ["Ledger", /#\/ledger$/, "Ledger"],
  ["Reports", /#\/reports$/, "Reports"],
  ["Periods", /#\/periods$/, /^Periods \d{4}$/],
  ["Settings", /#\/settings$/, "Settings"]
];

test("@ui-conformance every primary navigation link reaches its real destination at 320 and 1280, with intact labels (ULI-A01, ULI-L06)", async ({ app: { page } }) => {
  const nav = page.getByRole("navigation", { name: "Primary" });
  const menu = page.getByRole("button", { name: "Menu" });
  for (const width of [320, 1280]) {
    await setWidth(page, width);
    const narrow = width < 640;
    if (narrow) await menu.click();
    await expect(nav.getByRole("link")).toHaveText(NAVIGATION.map(([label]) => label));
    for (const [label, url, heading] of NAVIGATION) {
      // On a narrow screen the sections are behind the menu, which closes
      // on arrival, so it is opened for each destination.
      if (narrow && (await menu.getAttribute("aria-expanded")) !== "true") await menu.click();
      const link = nav.getByRole("link", { name: label, exact: true });
      // ULI-L06: the label is whole, not cropped.
      expect(await link.evaluate((a) => a.scrollWidth <= a.clientWidth && a.getBoundingClientRect().right <= document.documentElement.clientWidth), `${label} @${width}`).toBe(true);
      await link.click();
      await expect(page).toHaveURL(url);
      await expect(page.getByRole("main").getByRole("heading", { level: 1 })).toHaveText(heading);
      // Arrival closed the narrow menu; reopened, it marks where the person is.
      if (narrow) {
        await expect(menu).toHaveAttribute("aria-expanded", "false");
        await menu.click();
      }
      await expect(link).toHaveAttribute("aria-current", "page");
      await expect(page.getByRole("heading", { name: "Nothing is at this link" })).toHaveCount(0);
    }
  }
});

const ROUTES = ["#/", "#/work", "#/invoices", "#/proposals", "#/customers", "#/engagements", "#/payments", "#/credit-memos",
  "#/receivables", "#/ledger", "#/reports", "#/periods", "#/settings", "#/invoices/new", "#/follow-up", "#/inbox", "#/cpa", "#/no-such-page"];

test("@ui-conformance empty books: no route scrolls horizontally at 320, 390, 768 or 1280 (ULI-L05, ULI-L08, ULI-L09)", async ({ app: { page, origin } }) => {
  for (const width of WIDTHS) {
    await setWidth(page, width);
    for (const route of ROUTES) {
      await open(page, origin, route);
      await expectNoHorizontalOverflow(page, `${route} @${width}`);
    }
  }
});

test("@ui-conformance long labels and filled books: no route scrolls horizontally at any width (ULI-L05, ULI-L09)", async ({ app: { page, origin } }) => {
  test.setTimeout(120_000);
  await addCustomer(page, LONG_NAME, "Suite 4000, Extraordinarily-Long-Street-Name-Without-Any-Break-Opportunity-Whatsoever");
  await issueInvoice(page, LONG_NAME, "Discovery-workshop-and-requirements-analysis-for-the-entire-programme-portfolio", "123456.78");
  const invoice = new URL(page.url()).hash;
  for (const width of WIDTHS) {
    await setWidth(page, width);
    for (const route of [invoice, `${invoice}?tab=payments`, `${invoice}?tab=history`, "#/customers", "#/customers/CUST-0001", "#/invoices",
      "#/receivables", "#/ledger", "#/reports/trial-balance", "#/work", "#/invoices/new"]) {
      await open(page, origin, route);
      await expectNoHorizontalOverflow(page, `${route} @${width}`);
    }
  }
});

test("@ui-conformance 200% text: the editor, customers and home still reflow without horizontal scrolling (ULI-L05)", async ({ app: { page, origin } }) => {
  for (const width of [640, 1280]) {
    await setWidth(page, width);
    for (const route of ["#/", "#/customers", "#/invoices/new"]) {
      await open(page, origin, route);
      await page.addStyleTag({ content: "html { font-size: 200% !important; }" });
      await expectNoHorizontalOverflow(page, `${route} @${width} with 200% text`);
    }
  }
});

test("@ui-conformance keyboard: skip link reaches main, and a customer is added without a pointer (ULI-A08)", async ({ app: { page } }) => {
  await page.keyboard.press("Tab");
  const skip = page.getByRole("link", { name: "Skip to content" });
  await expect(skip).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#main$|#\/$/);

  await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Customers", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#\/customers$/);
  await page.getByRole("textbox", { name: "Name", exact: true }).focus();
  await page.keyboard.type("Keyed Ltd");
  // Enter in a text field submits its form natively.
  await page.keyboard.press("Enter");
  await expect(page.getByRole("link", { name: "Keyed Ltd" })).toBeVisible();
});

test("@ui-conformance reduced motion: transitions collapse when the person asks for less motion (ULI-L08, ULI-A06)", async ({ app: { page } }) => {
  const copy = page.getByRole("button", { name: "Copy link" });
  const duration = () => copy.evaluate((button) => Math.max(...getComputedStyle(button).transitionDuration.split(",").map((value) => parseFloat(value) * (value.trim().endsWith("ms") ? 1 : 1000))));
  expect(await duration()).toBeGreaterThan(1);
  await page.emulateMedia({ reducedMotion: "reduce" });
  await expect.poll(duration).toBeLessThanOrEqual(0.01);
  // The control still works with motion reduced: Copy link shows the address.
  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  await copy.click();
  await expect(page.getByRole("status")).toContainText(/[Ll]ink/);
});

// Enabled app actions no other browser test exercised before summa#90. Each
// asserts its own observable outcome: a download, a pressed state, a new
// address, or a row that appears and goes.
test("@ui-conformance remaining enabled actions have observable outcomes (ULI-A01, ULI-A02, ULI-A06, ULI-A10)", async ({ app: { page, origin } }) => {
  test.setTimeout(120_000);
  await addCustomer(page, "Acme");
  await issueInvoice(page, "Acme", "Assessment", "300");

  // Exports: real files, named for the invoice.
  for (const [name, extension] of [["Download (JSON)", ".json"], ["Download (HTML)", ".html"]]) {
    const download = page.waitForEvent("download");
    await page.getByRole("button", { name }).click();
    expect((await download).suggestedFilename()).toMatch(new RegExp(`INV-2026-0001.*\\${extension}$`));
  }
  // Tabs: the chosen one is pressed and named in the address.
  await expectActionOutcome(page, {
    action: field("button", "History"),
    outcome: { kind: "attribute", target: field("button", "History"), attribute: "aria-pressed", expected: "true" }
  });
  await expect(page).toHaveURL(/tab=history$/);

  await open(page, origin, "#/customers/CUST-0001");
  await expectActionOutcome(page, {
    action: field("button", "Credit memos"),
    outcome: { kind: "attribute", target: field("button", "Credit memos"), attribute: "aria-pressed", expected: "true" }
  });

  // The CPA workspace's year exports.
  await open(page, origin, "#/cpa");
  for (const name of ["Download journal (CSV)", "Download invoices (CSV)"]) {
    const download = page.waitForEvent("download");
    await page.getByRole("button", { name }).click();
    expect((await download).suggestedFilename()).toMatch(/\.csv$/);
  }
  await expectActionOutcome(page, { action: field("link", "Previous year"), outcome: { kind: "url", expected: /#\/cpa\?year=\d{4}$/ } });

  await open(page, origin, "#/reports/income-statement");
  await expectActionOutcome(page, {
    action: field("button", "Last month"),
    outcome: { kind: "attribute", target: field("button", "Last month"), attribute: "aria-pressed", expected: "true" }
  });

  await open(page, origin, "#/follow-up");
  await expectActionOutcome(page, {
    action: field("button", "All open"),
    outcome: { kind: "attribute", target: field("button", "All open"), attribute: "aria-pressed", expected: "true" }
  });

  await open(page, origin, "#/periods");
  await page.getByRole("link", { name: "Previous year" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(/^Periods \d{4}$/);
  await expect(page).toHaveURL(/#\/periods\?year=\d{4}$/);

  // Editor lines: each Remove is named for its line, so two are not one.
  await open(page, origin, "#/invoices/new");
  await expectActionOutcome(page, { action: field("button", "Add line"), outcome: { kind: "visible", target: field("textbox", "Line 2 description"), expected: true } });
  await expect(page.getByRole("button", { name: /^Remove line \d$/ })).toHaveCount(2);
  await page.getByRole("button", { name: "Remove line 2" }).click();
  await expect(page.getByRole("textbox", { name: "Line 2 description" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /^Remove line \d$/ })).toHaveCount(1);

  // Settings: a legal field is added as a row and removed by its own name.
  await open(page, origin, "#/settings");
  const legal = page.getByRole("region", { name: "Legal fields on invoices" });
  await legal.getByRole("textbox", { name: "Jurisdiction" }).fill("GB");
  await legal.getByRole("textbox", { name: "Value" }).fill("GB123456789");
  await legal.getByRole("button", { name: "Add legal field" }).click();
  await expect(legal.getByRole("cell", { name: "GB123456789" })).toBeVisible();
  const remove = legal.getByRole("button", { name: /^Remove .+ GB$/ });
  await expect(remove).toHaveCount(1);
  await remove.click();
  await expect(legal.getByRole("cell", { name: "GB123456789" })).toHaveCount(0);
});

// Formerly UNKNOWN in the evidence record (summa#95 follow-up, WI-0054).
test("@ui-conformance an invoice's dispute is recorded and resolved, each with its own outcome (ULI-A02, ULI-A07)", async ({ app: { page } }) => {
  await addCustomer(page, "Acme");
  await issueInvoice(page, "Acme", "Assessment", "300");
  await page.getByRole("button", { name: "Payments", exact: true }).click();
  const note = page.getByRole("textbox", { name: "What the customer disputes, or how it was resolved" });

  // Without a note nothing is recorded, and the page says why.
  await page.getByRole("button", { name: "Mark disputed" }).click();
  await expect(page.getByRole("alert")).toHaveText("Say what the customer disputes.");
  await expect(page.getByRole("button", { name: "Resolve the dispute" })).toHaveCount(0);

  await note.fill("Hours on 3 March");
  await expectActionOutcome(page, { action: field("button", "Mark disputed"), outcome: { kind: "visible", target: field("button", "Resolve the dispute"), expected: true } });
  await expect(page.getByRole("status")).toContainText("Dispute recorded.");
  await note.fill("Hours confirmed by the customer");
  await expectActionOutcome(page, { action: field("button", "Resolve the dispute"), outcome: { kind: "visible", target: field("button", "Mark disputed"), expected: true } });
  await expect(page.getByRole("status")).toContainText("Dispute resolved.");
});

test("@ui-conformance a follow-up row's Reminder sent is named for its invoice and records the reminder (ULI-A02, ULI-A06)", async ({ app: { page, origin } }) => {
  await addCustomer(page, "Acme");
  await issueInvoice(page, "Acme", "Assessment", "300");
  await open(page, origin, "#/follow-up");
  await page.getByRole("button", { name: "All open" }).click();
  const reminder = page.getByRole("button", { name: "Reminder sent for INV-2026-0001" });
  await expect(reminder).toBeVisible();
  await reminder.click();
  await expect(page.getByRole("status")).toContainText("Reminder recorded.");
  await open(page, origin, "#/invoices/INV-0001?tab=payments");
  await expect(page.locator("#follow-up-collection")).toContainText(/[Rr]eminde/);
});

test("@ui-conformance Print or save as PDF opens the browser's print dialog for the invoice (ULI-A02)", async ({ app: { page } }) => {
  await addCustomer(page, "Acme");
  await issueInvoice(page, "Acme", "Assessment", "300");
  // The outcome is the browser's own: window.print. It is observed by
  // counting calls, without opening a native dialog in the test browser.
  await page.evaluate(() => {
    window.__printCalls = 0;
    window.print = () => { window.__printCalls += 1; };
  });
  await page.getByRole("button", { name: "Print or save as PDF" }).click();
  await expect.poll(() => page.evaluate(() => window.__printCalls)).toBe(1);
  // What prints is the invoice's Folio document, and the screen controls do not.
  await page.emulateMedia({ media: "print" });
  await expect(page.getByRole("button", { name: "Print or save as PDF" })).toBeHidden();
  await expect(page.locator("ef-print-document")).toBeVisible();
});

test("@ui-conformance at 320 and 390 the primary sections fold behind a Menu disclosure; from 768 they are always shown (ULI-L09, ULI-A06, ULI-A08)", async ({ app: { page } }) => {
  const nav = page.getByRole("navigation", { name: "Primary" });
  const menu = page.getByRole("button", { name: "Menu" });
  const main = page.getByRole("main").getByRole("heading", { level: 1 });
  for (const width of [320, 390]) {
    await setWidth(page, width);
    await settleFontsAndFrames(page);
    // Closed: the sections take no rows; the header (brand and menu, the
    // search, and the two header actions) stays within half the first
    // screen, and the page's own heading is inside the first screen.
    await expect(menu).toHaveAttribute("aria-expanded", "false");
    await expect(menu).toHaveAttribute("aria-controls", "primary-nav");
    await expect(nav.getByRole("link")).toHaveCount(0);
    const screen = page.viewportSize().height;
    const header = await page.getByRole("banner").boundingBox();
    expect(header.y + header.height, `header bottom @${width}`).toBeLessThanOrEqual(screen / 2);
    const heading = await main.boundingBox();
    expect(heading.y + heading.height, `h1 bottom @${width}`).toBeLessThanOrEqual(screen);
    // Opened (by keyboard): every section, in order, one per row, full width.
    await menu.focus();
    await page.keyboard.press("Enter");
    await expect(menu).toHaveAttribute("aria-expanded", "true");
    await expect(nav.getByRole("link")).toHaveText(NAVIGATION.map(([label]) => label));
    const boxes = await nav.getByRole("link").evaluateAll((links) => links.map((a) => a.getBoundingClientRect().toJSON()));
    for (let i = 1; i < boxes.length; i += 1) expect(boxes[i].top, `section ${i + 1} below section ${i}`).toBeGreaterThanOrEqual(boxes[i - 1].bottom - 1);
    for (const box of boxes) expect(box.height).toBeGreaterThanOrEqual(44);
    await expectNoHorizontalOverflow(page, `open menu @${width}`);
    // Tab moves from the menu button into the sections.
    await page.keyboard.press("Tab");
    await expect(nav.getByRole("link", { name: "Home", exact: true })).toBeFocused();
    // Choosing a section goes there and closes the menu.
    await nav.getByRole("link", { name: "Customers", exact: true }).click();
    await expect(page).toHaveURL(/#\/customers$/);
    await expect(menu).toHaveAttribute("aria-expanded", "false");
    await expect(nav.getByRole("link")).toHaveCount(0);
    // The menu button closes it again without navigating.
    await menu.click();
    await menu.click();
    await expect(nav.getByRole("link")).toHaveCount(0);
    await expect(page).toHaveURL(/#\/customers$/);
  }
  for (const width of [768, 1280]) {
    await setWidth(page, width);
    await expect(menu).toBeHidden();
    await expect(nav.getByRole("link")).toHaveCount(NAVIGATION.length);
  }
});
