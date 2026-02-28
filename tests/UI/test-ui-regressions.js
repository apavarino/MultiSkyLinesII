const fs = require("fs");
const path = require("path");

function fail(message) {
  console.error(`FAIL: ${message}`);
  process.exit(1);
}

function assert(condition, message) {
  if (!condition) {
    fail(message);
  }
}

const sourcePath = path.resolve(__dirname, "../../UI/src/index.js");
const source = fs.readFileSync(sourcePath, "utf8");

// Regression guard: this exact signature previously caused runtime crash
// when components expected props object but received positional args.
const legacyTabSignature = /function\s+(OverviewTab|ContractsTab|DebugTab)\s*\(\s*data\s*,\s*t\s*\)/m;
assert(!legacyTabSignature.test(source), "Legacy tab signature `function X(data, t)` detected.");

// Ensure tabs are instantiated with object props.
assert(
  source.includes('React.createElement(OverviewTab, { data, ui })'),
  "OverviewTab must be created with object props `{ data, ui }`."
);
assert(
  source.includes('React.createElement(ContractsTab, { data, ui })'),
  "ContractsTab must be created with object props `{ data, ui }`."
);
assert(
  source.includes('React.createElement(DebugTab, { data, ui })'),
  "DebugTab must be created with object props `{ data, ui }`."
);

// Ensure helper translator is used instead of passing runtime function prop.
assert(
  source.includes("function tr(ui, key, fallback)"),
  "Translator helper `tr(ui, key, fallback)` is missing."
);
assert(
  !source.includes("const t = React.useCallback"),
  "Unexpected callback translator detected; risk of `t is not a function` regression."
);

console.log("UI regression checks passed.");
