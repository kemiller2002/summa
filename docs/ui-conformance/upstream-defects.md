# UI conformance: defects owned upstream

These defects were found while Summa adopted
`visual-engineering/UI-LAYOUT-INTERACTION@1.0.0` (summa#90, PR #95). They are
recorded here for the owning projects. Summa does not patch them in its own
copy of the code. Where Summa works around one in its own composition, the
workaround is named below.

## LIMEN-IF-NESTED: a `data-if` whose only root is another template is never unmounted

- **Rule:** ULI-L08 (state changes keep valid geometry). The visible effect is
  stale content, which also breaks ULI-A02, because a navigation appears to
  show the wrong page.
- **Owner:** Limen (`@echelon-foundry/limen`).
- **Version observed:** 0.9.0, the version Summa pins in `package-lock.json`.
- **Status in Summa:** worked around in `app/index.html`. The outer
  template's root is now `<div class="summa-route">` (`display: contents`).
  The regression test is `tests/browser/ui-conformance.app.spec.js`, "leaving
  an invoice unmounts it".

### Minimal reproduction

```html
<main>
  <template data-if="outer">
    <template data-if="inner">
      <section><h1>Shown while outer and inner</h1></section>
    </template>
  </template>
</main>
```

To reproduce:

1. Project `{ outer: true, inner: true }`. The section is shown, which is
   correct.
2. Project `{ outer: false, inner: true }`. **The section stays on screen.**
3. Project `{ outer: true, inner: true }` again. **A second copy of the
   section is mounted next to the first.**

In Summa, open an issued invoice (`#/invoices/INV-0001`), then choose any
other primary section. Before the workaround, that section's `h1` and the
invoice's `h1`, "Print or save as PDF" and the invoice document were all
shown. Returning to the invoice showed it twice.

### Cause (Limen 0.9.0 `dist/kernel/browser-kernel.js`)

- `#applyIf` takes the template content's `firstElementChild` as the mounted
  `root`, inserts it after the anchor, and calls `#bindElement(root, …)`.
  `HTMLTemplateElement` passes the `root instanceof HTMLElement` check.
- `#bindElement` treats that root as a nested `data-if`. It calls
  `el.replaceWith(anchor)`, so the inner template is replaced in the document
  by a comment anchor. The inner content is then mounted after that comment,
  as a sibling in the parent, not inside `root`.
- On unmount, `#applyIf` calls `binding.mounted.root.remove()`. The `root` is
  the inner `<template>`, already detached, so nothing is removed. The inner
  comment anchor and the inner mounted content stay in the parent. A later
  remount clones a new inner template and anchor, which produces the
  duplicate.

### Suggested fix

There are two options:

- Reject a template whose first element child is itself a `<template>`, with
  the same loud error Limen gives a template with no root element.
- Or track the mounted range by start and end anchors, so that unmounting
  removes everything the mount inserted.

Either option makes the failure impossible to hit silently.

## FORMA-CONTROL-HEIGHT: text inputs and native selects differ in height in one row

- **Rule:** ULI-L02 (control geometry), and ULI-L03 (primitives own their
  internal geometry).
- **Owner:** Forma (`@echelon-foundry/design-system`). Observed in 0.6.1.
- **Cause:** `:where(input:not([type=checkbox],[type=radio],[type=range]), textarea, select)`
  sets `min-block-size: 2.75rem` and the same padding, but not `box-sizing`.
  The browser renders a text input as content-box, 50 CSS px, and a select as
  border-box, 46.4 CSS px.
- **In Summa:** not patched. `tests/browser/ui-conformance.app.spec.js`
  "equivalent controls in a declared row have equal heights" is marked
  `test.fail`, so it starts failing when a fixed Forma is pinned.

## FORMA-FIELD-STRETCH: `.ef-field` spreads stretched height between label and control

- **Rule:** ULI-L01 (peer fields share a top edge).
- **Owner:** Forma. Observed in 0.6.1.
- **Cause:** `.ef-field` is an auto-row grid without `align-content: start`.
  When a parent stretches the field to the height of a neighbour that has
  help text, the extra height goes partly above the control, so the control
  drops below its peers' controls.
- **In Summa:** the composition fixes the relationship it owns.
  `.summa-fields` and `.form-row` use `align-items: start`.

## FORMA-TABLE-WRAP: `overflow-wrap: anywhere` on `td` and `th` splits labels mid-word

- **Rule:** ULI-L05.
- **Owner:** Forma. Observed in 0.6.1.
- **Cause:** `:where(h1, …, td, th) { overflow-wrap: anywhere }` lowers a
  cell's min-content width to a single letter. An auto-layout table then
  shrinks its columns until headers and button labels break between letters.
- **In Summa:** the line editor's table uses `overflow-wrap: break-word`,
  scoped to `.summa-lines`.

## FORMA-GRID-MIN-SIZE: `.ef-data-grid` widens a flex column instead of scrolling

- **Rule:** ULI-L05 (no horizontal page scrolling; essential 2-D content may have a contained, labelled scroller).
- **Owner:** Forma. Observed in 0.6.1, after Summa adopted the data grid (summa#101).
- **Cause:** `.ef-data-grid` sets `overflow-x: auto` and its table `min-inline-size: 42rem`, but the grid itself keeps `min-inline-size: auto`. As a flex item (for example inside `.ef-stack`), its automatic minimum is the table's 42rem, so the grid grows past its column and the page scrolls. At 1280 CSS px with 200% text, the invoice editor's lines grid pushed the page 65 CSS px wide. `.ef-surface` already sets `min-inline-size: 0` for this reason.
- **A second cause, in the same grid:** `.ef-data-grid` is `position: static`. Absolutely positioned descendants, such as Forma's own `.ef-visually-hidden` cell labels, take their containing block from outside the scroller, so they escape it. Those labels alone made the document 65 CSS px wider than the viewport. The grid should be `position: relative`.
- **In Summa:** `.summa-main .ef-data-grid { min-inline-size: 0; position: relative }` and `.summa-main > * { min-inline-size: 0 }`, both composition fixes. The regression test is "200% text: the editor, customers and home still reflow without horizontal scrolling".
