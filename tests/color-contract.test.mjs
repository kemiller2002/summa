import assert from "node:assert/strict";
import test from "node:test";

import { validateCssColorReferences } from "@echelon-foundry/design-system/color-contract";

const unsupported = [
  "--ef-color-action-primary",
  "--ef-color-text-link",
  "--ef-color-focus",
  "--ef-color-text-danger",
  "--ef-color-text-muted",
];

for (const token of unsupported) {
  test(`Forma rejects Summa's former unsupported token ${token}`, () => {
    const findings = validateCssColorReferences(`.probe { color: var(${token}); }`);

    assert.equal(findings.length, 1);
    assert.equal(findings[0].code, "COLOR_UNKNOWN_FORMA_TOKEN");
    assert.equal(findings[0].token, token);
  });
}

test("Forma rejects a dark-theme value frozen as a light-theme fallback", () => {
  const findings = validateCssColorReferences(
    ".probe { color: var(--ef-color-text-primary, #f2efe7); }",
  );

  assert.equal(findings.length, 1);
  assert.equal(findings[0].code, "COLOR_LITERAL_FALLBACK_MISMATCH");
  assert.equal(findings[0].token, "--ef-color-text-primary");
});
