import assert from "node:assert/strict";
import test from "node:test";

import { validateCssColorReferences } from "@echelon-foundry/design-system/color-contract";

const unsupported = [
  "--forma-color-action-primary",
  "--forma-color-text-link",
  "--forma-color-focus",
  "--forma-color-text-danger",
  "--forma-color-text-muted",
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
    ".probe { color: var(--forma-color-text-primary, #f2efe7); }",
  );

  assert.equal(findings.length, 1);
  assert.equal(findings[0].code, "COLOR_LITERAL_FALLBACK_MISMATCH");
  assert.equal(findings[0].token, "--forma-color-text-primary");
});
