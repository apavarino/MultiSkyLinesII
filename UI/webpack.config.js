const path = require("path");
const mod = require("./mod.json");

const userDataPath = process.env.CSII_USERDATAPATH;
if (!userDataPath) {
  throw new Error("CSII_USERDATAPATH is not set.");
}

module.exports = {
  mode: "production",
  entry: {
    [mod.id]: path.resolve(__dirname, "src/index.js")
  },
  output: {
    path: path.resolve(userDataPath, "Mods", mod.id),
    filename: "[name].mjs",
    library: {
      type: "module"
    },
    publicPath: "coui://ui-mods/"
  },
  experiments: {
    outputModule: true
  },
  externalsType: "window",
  externals: {
    react: "React",
    "react-dom": "ReactDOM",
    "cs2/modding": "cs2/modding",
    "cs2/api": "cs2/api",
    "cs2/bindings": "cs2/bindings",
    "cs2/l10n": "cs2/l10n",
    "cs2/ui": "cs2/ui",
    "cs2/input": "cs2/input",
    "cs2/utils": "cs2/utils",
    "cohtml/cohtml": "cohtml/cohtml"
  }
};
