const fs = require("fs");
const path = require("path");

const mod = require("../mod.json");
const userDataPath = process.env.CSII_USERDATAPATH;

if (!userDataPath) {
  throw new Error("CSII_USERDATAPATH is not set.");
}

const outputDir = path.resolve(userDataPath, "Mods", mod.id);
const outputPath = path.join(outputDir, "mod.json");
const packagePath = path.join(outputDir, "package.json");
const legacyBuildDir = path.join(outputDir, "build");
const legacyBundleJs = path.join(outputDir, `${mod.id}.js`);

fs.mkdirSync(outputDir, { recursive: true });
fs.copyFileSync(path.resolve(__dirname, "../mod.json"), outputPath);
if (fs.existsSync(packagePath)) {
  fs.rmSync(packagePath, { force: true });
}
if (fs.existsSync(legacyBuildDir)) {
  fs.rmSync(legacyBuildDir, { recursive: true, force: true });
}
if (fs.existsSync(legacyBundleJs)) {
  fs.rmSync(legacyBundleJs, { force: true });
}

console.log(`Copied mod.json -> ${outputPath}`);
console.log(`Cleaned legacy UI artifacts in -> ${outputDir}`);
