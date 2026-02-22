import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { Portal } from "cs2/ui";

const GROUP = "multisky";
const payload$ = bindValue(GROUP, "payload", "{}");
const visible$ = bindValue(GROUP, "visible", false);

function safeTrigger(name, ...args) {
  try {
    const normalized = [];
    for (let i = 0; i < args.length; i++) {
      if (args[i] !== undefined) {
        normalized.push(args[i]);
      }
    }
    trigger(GROUP, name, ...normalized);
    return true;
  } catch (e) {
    bridgeHeartbeat(`trigger error ${name}: ${e && e.message ? e.message : String(e)}`);
    return false;
  }
}

function bridgeHeartbeat(message) {
  try {
    safeTrigger("uiPing", message);
    safeTrigger("uiReady");
  } catch (_e) {
  }
  try {
    if (typeof engine !== "undefined" && engine && typeof engine.trigger === "function") {
      engine.trigger(GROUP, "uiPing", message);
      engine.trigger(GROUP, "uiReady");
    }
  } catch (_e) {
  }
}

bridgeHeartbeat("module bootstrap");
setInterval(() => bridgeHeartbeat("module heartbeat"), 2000);

function safeParsePayload(payloadText) {
  try {
    if (!payloadText || typeof payloadText !== "string") {
      return {};
    }
    return JSON.parse(payloadText);
  } catch (_e) {
    return {};
  }
}

function resourceLabel(resource) {
  switch (resource) {
    case 0:
      return "Electricite";
    case 1:
      return "Eau";
    case 2:
      return "Eaux usees";
    default:
      return "Inconnue";
  }
}

function rate(resource, units) {
  const n = Number(units) || 0;
  if (resource === 0) {
    const kw = n / 10;
    const mw = kw / 1000;
    if (Math.abs(mw) >= 1000) return `${(mw / 1000).toFixed(2)} GW/tick`;
    if (Math.abs(mw) >= 1) return `${mw.toFixed(2)} MW/tick`;
    return `${kw.toFixed(2)} kW/tick`;
  }
  return `${n} m3/tick`;
}

function panelStyle() {
  return {
    position: "absolute",
    top: "82rem",
    right: "22rem",
    width: "640rem",
    height: "530rem",
    background: "linear-gradient(170deg, rgba(20,33,47,0.96), rgba(12,19,30,0.96))",
    border: "1rem solid rgba(127,173,214,0.55)",
    borderRadius: "14rem",
    boxShadow: "0 16rem 42rem rgba(0,0,0,0.55)",
    color: "#e6f1ff",
    pointerEvents: "auto",
    overflow: "hidden"
  };
}

const styles = {
  shell: panelStyle(),
  header: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    padding: "14rem 16rem 10rem 16rem",
    borderBottom: "1rem solid rgba(126,170,209,0.3)"
  },
  title: { fontSize: "17rem", fontWeight: 700, letterSpacing: "0.2rem" },
  meta: { fontSize: "11rem", color: "#9ab5d1" },
  tabs: { display: "flex", gap: "8rem", padding: "10rem 12rem" },
  tab: {
    border: "1rem solid rgba(120,160,196,0.5)",
    borderRadius: "9rem",
    background: "rgba(44,72,100,0.52)",
    color: "#dbe9f8",
    padding: "6rem 12rem",
    fontSize: "12rem",
    cursor: "pointer"
  },
  tabActive: {
    border: "1rem solid rgba(143,188,227,0.9)",
    background: "rgba(72,122,172,0.66)",
    color: "#f2f8ff"
  },
  content: {
    height: "430rem",
    overflowY: "auto",
    padding: "8rem 12rem 12rem 12rem"
  },
  card: {
    background: "rgba(32,51,73,0.66)",
    border: "1rem solid rgba(110,150,188,0.38)",
    borderRadius: "10rem",
    padding: "10rem 12rem",
    marginBottom: "10rem"
  },
  row: { display: "flex", justifyContent: "space-between", gap: "12rem", fontSize: "12rem", marginBottom: "4rem" },
  muted: { color: "#9eb7d3", fontSize: "11rem" },
  btnRow: { display: "flex", gap: "8rem", marginTop: "8rem", flexWrap: "wrap" },
  actionBtn: {
    border: "1rem solid rgba(133,178,217,0.7)",
    borderRadius: "8rem",
    background: "rgba(75,123,172,0.64)",
    color: "#eff7ff",
    fontSize: "11rem",
    padding: "5rem 9rem",
    cursor: "pointer"
  }
};

function TabButton(props) {
  const merged = props.active ? { ...styles.tab, ...styles.tabActive } : styles.tab;
  return React.createElement("button", { type: "button", onClick: props.onClick, style: merged }, props.label);
}

function OverviewTab(data) {
  const states = Array.isArray(data.states) ? data.states : [];
  return React.createElement(
    React.Fragment,
    null,
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, "Session"), React.createElement("span", { style: styles.muted }, `v${data.version || "?"}`)),
      React.createElement("div", { style: styles.row }, React.createElement("span", null, `Mode: ${data.mode || "?"}`), React.createElement("span", { style: styles.muted }, data.destination || "?")),
      React.createElement("div", { style: styles.muted }, `Local: ${data.localName || "Unknown"}`)
    ),
    states.map((s, i) =>
      React.createElement(
        "div",
        { key: `st-${i}`, style: styles.card },
        React.createElement("div", { style: styles.row }, React.createElement("strong", null, s.name || "Unknown"), React.createElement("span", { style: styles.muted }, `Ping ${s.pingMs != null ? s.pingMs : "n/a"} ms`)),
        React.createElement("div", { style: styles.row }, React.createElement("span", null, `Population ${Number(s.population || 0).toLocaleString()}`), React.createElement("span", null, `$${Number(s.money || 0).toLocaleString()}`)),
        React.createElement("div", { style: styles.muted }, `Elec: ${rate(0, s.elecServed || 0)} / demande ${rate(0, s.elecCons || 0)}`),
        React.createElement("div", { style: styles.muted }, `Eau: ${rate(1, s.waterServed || 0)} / demande ${rate(1, s.waterCons || 0)}`),
        React.createElement("div", { style: styles.muted }, `Eaux usees: ${rate(2, s.sewServed || 0)} / demande ${rate(2, s.sewCons || 0)}`)
      )
    )
  );
}

function ContractsTab(data) {
  const contracts = Array.isArray(data.contracts) ? data.contracts : [];
  const proposals = Array.isArray(data.proposals) ? data.proposals : [];
  const states = Array.isArray(data.states) ? data.states : [];
  const localName = data.localName || "";
  const defaultSeller = React.useMemo(() => {
    const first = states.find((s) => s && s.name && s.name !== localName);
    return first ? first.name : "";
  }, [states, localName]);

  const sendProposal = React.useCallback(() => {
    if (!defaultSeller || !localName) return;
    const packed = `${defaultSeller}|${localName}|0|5000|1000`;
    safeTrigger("propose", packed);
  }, [defaultSeller, localName]);

  return React.createElement(
    React.Fragment,
    null,
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, `Contrats actifs (${contracts.length})`)),
      contracts.length === 0
        ? React.createElement("div", { style: styles.muted }, "Aucun contrat actif.")
        : contracts.map((c) =>
            React.createElement(
              "div",
              { key: `c-${c.id}`, style: { ...styles.card, marginBottom: "8rem" } },
              React.createElement("div", { style: styles.row }, React.createElement("span", null, `${c.seller} -> ${c.buyer}`), React.createElement("span", { style: styles.muted }, `$${c.pricePerTick}/tick`)),
              React.createElement("div", { style: styles.muted }, `${resourceLabel(c.resource)} ${rate(c.resource, c.unitsPerTick)}`),
              React.createElement(
                "div",
                { style: styles.btnRow },
                React.createElement("button", {
                  type: "button",
                  style: styles.actionBtn,
                  onClick: () => {
                    if (c.id == null) return;
                    safeTrigger("cancel", c.id);
                  }
                }, "Annuler")
              )
            )
          )
    ),
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, `Demandes (${proposals.length})`)),
      proposals.length === 0
        ? React.createElement("div", { style: styles.muted }, "Aucune demande en attente.")
        : proposals.map((p) =>
            React.createElement(
              "div",
              { key: `p-${p.id}`, style: { ...styles.card, marginBottom: "8rem" } },
              React.createElement("div", { style: styles.row }, React.createElement("span", null, `${p.buyer} -> ${p.seller}`), React.createElement("span", { style: styles.muted }, `${Math.max(0, 120 - Number(p.ageSeconds || 0))}s`)),
              React.createElement("div", { style: styles.muted }, `${resourceLabel(p.resource)} ${rate(p.resource, p.unitsPerTick)} | $${p.pricePerTick}/tick`),
              React.createElement(
                "div",
                { style: styles.btnRow },
                React.createElement("button", {
                  type: "button",
                  style: styles.actionBtn,
                  onClick: () => {
                    if (p.id == null) return;
                    safeTrigger("respond", p.id, true);
                  }
                }, "Accepter"),
                React.createElement("button", {
                  type: "button",
                  style: styles.actionBtn,
                  onClick: () => {
                    if (p.id == null) return;
                    safeTrigger("respond", p.id, false);
                  }
                }, "Refuser")
              )
            )
          )
    ),
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, "Proposer un contrat (rapide)")),
      React.createElement("div", { style: styles.muted }, defaultSeller ? `Vendeur: ${defaultSeller}` : "Aucune ville distante disponible."),
      React.createElement(
        "div",
        { style: styles.btnRow },
        React.createElement("button", { type: "button", style: styles.actionBtn, onClick: sendProposal, disabled: !defaultSeller }, "Envoyer offre test (Elec 5000 / $1000)")
      )
    )
  );
}

function DebugTab(data) {
  const logs = Array.isArray(data.logs) ? data.logs : [];
  const message = data.message || "";
  return React.createElement(
    React.Fragment,
    null,
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, "Debug"), React.createElement("span", { style: styles.muted }, `${logs.length} lignes`)),
      message ? React.createElement("div", { style: { ...styles.muted, marginBottom: "8rem", color: "#ffe3a8" } }, message) : null,
      React.createElement(
        "div",
        { style: styles.btnRow },
        React.createElement("button", { type: "button", style: styles.actionBtn, onClick: () => safeTrigger("clearLogs") }, "Clear logs"),
        React.createElement("button", { type: "button", style: styles.actionBtn, onClick: () => safeTrigger("uiPing", "manual ping from UI") }, "Ping bridge")
      )
    ),
    React.createElement(
      "div",
      { style: styles.card },
      logs.length === 0
        ? React.createElement("div", { style: styles.muted }, "Aucun log reseau.")
        : logs.map((line, i) => React.createElement("div", { key: `l-${i}`, style: { ...styles.muted, marginBottom: "4rem", color: "#cfe3f8" } }, line))
    )
  );
}

function MultiplayerPanel() {
  const visible = useValue(visible$);
  const payloadText = useValue(payload$);
  const [tab, setTab] = React.useState("overview");
  const data = React.useMemo(() => safeParsePayload(payloadText), [payloadText]);

  React.useEffect(() => {
    safeTrigger("uiReady");
    safeTrigger("uiPing", "native ui mounted");
  }, []);

  if (!visible) {
    return null;
  }

  let content = React.createElement(OverviewTab, data);
  if (tab === "contracts") content = React.createElement(ContractsTab, data);
  if (tab === "debug") content = React.createElement(DebugTab, data);

  return React.createElement(
    Portal,
    null,
    React.createElement(
      "div",
      { style: styles.shell },
      React.createElement(
        "div",
        { style: styles.header },
        React.createElement("div", null, React.createElement("div", { style: styles.title }, "MultiSkyLineII"), React.createElement("div", { style: styles.meta }, `${data.mode || "?"} | ${data.destination || "?"}`)),
        React.createElement("button", { type: "button", style: styles.actionBtn, onClick: () => safeTrigger("setVisible", false) }, "Fermer")
      ),
      React.createElement(
        "div",
        { style: styles.tabs },
        React.createElement(TabButton, { label: "Vue", active: tab === "overview", onClick: () => setTab("overview") }),
        React.createElement(TabButton, { label: "Contrats", active: tab === "contracts", onClick: () => setTab("contracts") }),
        React.createElement(TabButton, { label: "Debug", active: tab === "debug", onClick: () => setTab("debug") })
      ),
      React.createElement("div", { style: styles.content }, content)
    )
  );
}

function LauncherButton() {
  const visible = useValue(visible$);
  React.useEffect(() => {
    safeTrigger("uiReady");
    safeTrigger("uiPing", "launcher mounted");
  }, []);

  return React.createElement(
    Portal,
    null,
    React.createElement(
      "div",
      {
        style: {
          position: "absolute",
          top: "18rem",
          right: "18rem",
          pointerEvents: "auto",
          zIndex: 50
        }
      },
      React.createElement(
        "button",
        {
          type: "button",
          onClick: () => safeTrigger("setVisible", !visible),
          style: {
            border: "1rem solid rgba(133,178,217,0.78)",
            borderRadius: "10rem",
            background: visible ? "rgba(76,129,181,0.95)" : "rgba(45,74,105,0.9)",
            color: "#eef7ff",
            padding: "7rem 13rem",
            fontSize: "12rem",
            fontWeight: 700,
            letterSpacing: "0.2rem",
            cursor: "pointer",
            boxShadow: "0 6rem 16rem rgba(0,0,0,0.35)"
          }
        },
        visible ? "MS2 OUVERT" : "MS2"
      )
    )
  );
}

const register = (moduleRegistry) => {
  moduleRegistry.append("GameTopRight", LauncherButton);
  moduleRegistry.append("Game", MultiplayerPanel);
};

export default register;
