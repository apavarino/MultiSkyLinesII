import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { Portal } from "cs2/ui";

const GROUP = "multisky";
const payload$ = bindValue(GROUP, "payload", "{}");
const visible$ = bindValue(GROUP, "visible", false);

function tr(ui, key, fallback) {
  const v = ui && ui[key];
  return typeof v === "string" && v.length > 0 ? v : fallback;
}

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

function packRespond(id, accept) {
  if (id == null) return "";
  return `${id}|${accept ? "1" : "0"}`;
}

function bridgeHeartbeat(message) {
  try {
    safeTrigger("uiPing", message);
  } catch (_e) {
  }
  try {
    if (typeof engine !== "undefined" && engine && typeof engine.trigger === "function") {
      engine.trigger(GROUP, "uiPing", message);
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
      return "Electricity";
    case 1:
      return "Water";
    case 2:
      return "Sewage";
    default:
      return "Unknown";
  }
}

function resourceName(resource) {
  return resourceLabel(Number(resource));
}

function formatNumber(value, fractionDigits = 0) {
  const n = Number(value);
  const safe = Number.isFinite(n) ? n : 0;
  const fixed = safe.toFixed(Math.max(0, fractionDigits));
  const parts = fixed.split(".");
  const sign = parts[0].startsWith("-") ? "-" : "";
  const intRaw = sign ? parts[0].slice(1) : parts[0];
  const intGrouped = intRaw.replace(/\B(?=(\d{3})+(?!\d))/g, " ");
  if (parts.length === 1 || fractionDigits <= 0) {
    return `${sign}${intGrouped}`;
  }
  return `${sign}${intGrouped}.${parts[1]}`;
}

function formatMoney(value) {
  return `$${formatNumber(value, 0)}`;
}

function rate(resource, units) {
  const n = Number(units) || 0;
  if (resource === 0) {
    const kw = n / 10;
    const mw = kw / 1000;
    if (Math.abs(mw) >= 1000) return `${formatNumber(mw / 1000, 2)} GW/tick`;
    if (Math.abs(mw) >= 1) return `${formatNumber(mw, 2)} MW/tick`;
    return `${formatNumber(kw, 2)} kW/tick`;
  }
  return `${formatNumber(n, 0)} m3/tick`;
}

function getResourceMaxAvailable(state, resource) {
  if (!state) return 0;
  if (resource === 0) return Math.max(0, Number(state.elecProd || 0) - Number(state.elecCons || 0));
  if (resource === 1) return Math.max(0, Number(state.waterCap || 0) - Number(state.waterCons || 0));
  return Math.max(0, Number(state.sewCap || 0) - Number(state.sewCons || 0));
}

function panelStyle() {
  return {
    position: "absolute",
    top: "50%",
    left: "50%",
    transform: "translate(-50%, -50%)",
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

function OverviewTab({ data, ui }) {
  const states = Array.isArray(data.states) ? data.states : [];
  return React.createElement(
    React.Fragment,
    null,
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, tr(ui, "session", "Session")), React.createElement("span", { style: styles.muted }, `v${data.version || "?"}`)),
      React.createElement("div", { style: styles.row }, React.createElement("span", null, `Mode: ${data.mode || "?"}`), React.createElement("span", { style: styles.muted }, data.destination || "?")),
      React.createElement("div", { style: styles.muted }, `${tr(ui, "local", "Local")}: ${data.localName || "Unknown"}`)
    ),
    states.map((s, i) =>
      React.createElement(
        "div",
        { key: `st-${i}`, style: styles.card },
        React.createElement("div", { style: styles.row }, React.createElement("strong", null, s.name || "Unknown"), React.createElement("span", { style: styles.muted }, `Ping ${s.pingMs != null ? s.pingMs : "n/a"} ms`)),
        React.createElement("div", { style: styles.row }, React.createElement("span", null, `Population ${formatNumber(s.population || 0)}`), React.createElement("span", null, formatMoney(s.money || 0))),
        React.createElement("div", { style: styles.muted }, `Elec: ${rate(0, s.elecServed || 0)} / demande ${rate(0, s.elecCons || 0)}`),
        React.createElement("div", { style: styles.muted }, `Eau: ${rate(1, s.waterServed || 0)} / demande ${rate(1, s.waterCons || 0)}`),
        React.createElement("div", { style: styles.muted }, `Eaux usees: ${rate(2, s.sewServed || 0)} / demande ${rate(2, s.sewCons || 0)}`)
      )
    )
  );
}

function ContractsTab({ data, ui }) {
  const contracts = Array.isArray(data.contracts) ? data.contracts : [];
  const proposals = Array.isArray(data.proposals) ? data.proposals : [];
  const states = Array.isArray(data.states) ? data.states : [];
  const localName = data.localName || "";
  const localState = React.useMemo(() => states.find((s) => s && s.name === localName) || null, [states, localName]);
  const [resource, setResource] = React.useState(0);
  const [units, setUnits] = React.useState(0);
  const [price, setPrice] = React.useState(1000);
  const committedOutgoing = React.useMemo(() => {
    return contracts
      .filter((c) => c && c.seller === localName && Number(c.resource) === Number(resource))
      .reduce((acc, c) => acc + Math.max(0, Number(c.unitsPerTick || 0)), 0);
  }, [contracts, localName, resource]);
  const maxAvailableRaw = getResourceMaxAvailable(localState, resource);
  const maxOffer = Math.max(0, maxAvailableRaw - committedOutgoing);
  const unitStep = resource === 0 ? 100 : 10;

  React.useEffect(() => {
    if (maxOffer <= 0) {
      setUnits(0);
      return;
    }
    if (units <= 0) {
      setUnits(Math.min(Math.max(unitStep, 1000), maxOffer));
      return;
    }
    if (units > maxOffer) {
      setUnits(maxOffer);
    }
  }, [maxOffer, units, unitStep]);

  const sendProposal = React.useCallback(() => {
    if (!localName) return;
    const safeUnits = Math.max(1, Math.min(maxOffer, Number(units) || 0));
    const safePrice = Math.max(1, Number(price) || 1);
    if (safeUnits <= 0) return;
    const packed = `${localName}||${resource}|${safeUnits}|${safePrice}`;
    safeTrigger("propose", packed);
  }, [localName, resource, units, price, maxOffer]);

  return React.createElement(
    React.Fragment,
    null,
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, `${tr(ui, "active_contracts", "Active contracts")} (${contracts.length})`)),
      contracts.length === 0
        ? React.createElement("div", { style: styles.muted }, tr(ui, "none_active_contracts", "No active contracts."))
        : contracts.map((c) =>
            React.createElement(
              "div",
              { key: `c-${c.id}`, style: { ...styles.card, marginBottom: "8rem" } },
              React.createElement("div", { style: styles.row }, React.createElement("span", null, `${c.seller} -> ${c.buyer}`), React.createElement("span", { style: styles.muted }, `${formatMoney(c.pricePerTick)}/tick`)),
              React.createElement("div", { style: styles.muted }, `${resourceName(c.resource)} ${rate(c.resource, c.unitsPerTick)}`),
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
                }, tr(ui, "cancel", "Cancel"))
              )
            )
          )
    ),
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, `${tr(ui, "pending_proposals", "Pending proposals")} (${proposals.length})`)),
      proposals.length === 0
        ? React.createElement("div", { style: styles.muted }, tr(ui, "none_pending_proposals", "No pending proposals."))
        : proposals.map((p) =>
            React.createElement(
              "div",
              { key: `p-${p.id}`, style: { ...styles.card, marginBottom: "8rem" } },
              React.createElement("div", { style: styles.row }, React.createElement("span", null, `${p.seller} -> ${p.buyer || "PUBLIC"}`), React.createElement("span", { style: styles.muted }, `${formatNumber(Math.max(0, 120 - Number(p.ageSeconds || 0)))}s`)),
              React.createElement("div", { style: styles.muted }, `${resourceName(p.resource)} ${rate(p.resource, p.unitsPerTick)} | ${formatMoney(p.pricePerTick)}/tick`),
              React.createElement(
                "div",
                { style: styles.btnRow },
                React.createElement("button", {
                  type: "button",
                  style: styles.actionBtn,
                  onClick: () => {
                    if (p.id == null) return;
                    safeTrigger("respond", packRespond(p.id, true));
                  }
                }, tr(ui, "accept", "Accept")),
                React.createElement("button", {
                  type: "button",
                  style: styles.actionBtn,
                  onClick: () => {
                    if (p.id == null) return;
                    safeTrigger("respond", packRespond(p.id, false));
                  }
                }, tr(ui, "reject", "Reject"))
              )
            )
          )
    ),
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, tr(ui, "public_offer_title", "Propose a public service offer"))),
      React.createElement("div", { style: styles.muted }, tr(ui, "public_offer_desc", "Any interested player can accept.")),
      React.createElement("div", { style: styles.muted }, `Ressource: ${resourceName(resource)} | Max vendable: ${rate(resource, maxOffer)}`),
      React.createElement("div", { style: styles.muted }, `Quantite: ${rate(resource, units)} | Prix: ${formatMoney(price)}/tick`),
      React.createElement(
        "div",
        { style: { ...styles.btnRow, flexDirection: "column", alignItems: "stretch" } },
        React.createElement("button", { type: "button", style: styles.actionBtn, onClick: () => setResource((resource + 1) % 3) }, tr(ui, "change_resource", "Change resource")),
        React.createElement("input", {
          type: "range",
          min: 0,
          max: Math.max(0, maxOffer),
          step: unitStep,
          value: Math.min(units, Math.max(0, maxOffer)),
          onChange: (e) => setUnits(Number(e.target.value || 0))
        }),
        React.createElement("input", {
          type: "range",
          min: 1,
          max: 20000,
          step: 50,
          value: Math.max(1, price),
          onChange: (e) => setPrice(Number(e.target.value || 1))
        }),
        React.createElement("button", {
          type: "button",
          style: styles.actionBtn,
          onClick: sendProposal,
          disabled: maxOffer <= 0 || units <= 0
        }, tr(ui, "send_offer", "Send offer"))
      )
    )
  );
}

function humanizeDebugLine(line) {
  if (!line) return "";
  let out = String(line);
  out = out.replace(/\bTX\b/g, "Sortant");
  out = out.replace(/\bRX\b/g, "Entrant");
  out = out.replace(/\bSTATE\b/g, "Etat");
  out = out.replace(/\bCONTRACTREQ\b/g, "DemandeContrat");
  out = out.replace(/\bCONTRACTDECISION\b/g, "DecisionContrat");
  out = out.replace(/\bCONTRACTCANCEL\b/g, "AnnulationContrat");
  out = out.replace(/\bCONTRACTS\b/g, "Contrats");
  out = out.replace(/\bPROPOSALS\b/g, "Propositions");
  out = out.replace(/\bSETTLES\b/g, "Reglements");
  out = out.replace(/\bPINGREQ\b/g, "PingReq");
  out = out.replace(/\bPINGRSP\b/g, "PingRsp");
  out = out.replace("LIST/CONTRACTS/PROPOSALS/SETTLES", "Snapshot complet");
  return out;
}

function DebugTab({ data, ui }) {
  const logs = Array.isArray(data.logs) ? data.logs : [];
  const message = data.message || "";
  const contractsFlag = data && Object.prototype.hasOwnProperty.call(data, "contractsEnabledDebug")
    ? String(data.contractsEnabledDebug)
    : "n/a";
  return React.createElement(
    React.Fragment,
    null,
    React.createElement(
      "div",
      { style: styles.card },
      React.createElement("div", { style: styles.row }, React.createElement("strong", null, "Debug"), React.createElement("span", { style: styles.muted }, `${logs.length} lignes`)),
      React.createElement("div", { style: styles.muted }, `contractsEnabled=${contractsFlag}`),
      message ? React.createElement("div", { style: { ...styles.muted, marginBottom: "8rem", color: "#ffe3a8" } }, message) : null,
      React.createElement(
        "div",
        { style: styles.btnRow },
        React.createElement("button", { type: "button", style: styles.actionBtn, onClick: () => safeTrigger("clearLogs") }, tr(ui, "clear_logs", "Clear logs")),
        React.createElement("button", { type: "button", style: styles.actionBtn, onClick: () => safeTrigger("uiPing", "manual ping from UI") }, tr(ui, "ping_bridge", "Ping bridge"))
      )
    ),
    React.createElement(
      "div",
      { style: styles.card },
      logs.length === 0
        ? React.createElement("div", { style: styles.muted }, tr(ui, "no_network_logs", "No network logs."))
        : logs.map((line, i) => React.createElement("div", { key: `l-${i}`, style: { ...styles.muted, marginBottom: "4rem", color: "#cfe3f8" } }, humanizeDebugLine(line)))
    )
  );
}

function MultiplayerPanel() {
  const visible = useValue(visible$);
  const payloadText = useValue(payload$);
  const [tab, setTab] = React.useState("overview");
  const data = React.useMemo(() => safeParsePayload(payloadText), [payloadText]);
  const ui = data.ui || {};

  React.useEffect(() => {
    safeTrigger("uiReady");
    safeTrigger("uiPing", "native ui mounted");
  }, []);

  if (!visible) {
    return null;
  }

  let content = React.createElement(OverviewTab, { data, ui });
  if (tab === "contracts") content = React.createElement(ContractsTab, { data, ui });
  if (tab === "debug") content = React.createElement(DebugTab, { data, ui });

  return React.createElement(
    Portal,
    null,
    React.createElement(
      "div",
      { style: styles.shell },
      React.createElement(
        "div",
        { style: styles.header },
        React.createElement("div", null, React.createElement("div", { style: styles.title }, tr(ui, "title", "MultiSkyLines II")), React.createElement("div", { style: styles.meta }, `Version ${data.version || "?"}`)),
        React.createElement("button", { type: "button", style: styles.actionBtn, onClick: () => safeTrigger("toggleVisible") }, tr(ui, "close", "Close"))
      ),
      React.createElement(
        "div",
        { style: styles.tabs },
        React.createElement(TabButton, { label: tr(ui, "tab_overview", "Overview"), active: tab === "overview", onClick: () => setTab("overview") }),
        React.createElement(TabButton, { label: tr(ui, "tab_contracts", "Contracts"), active: tab === "contracts", onClick: () => setTab("contracts") }),
        React.createElement(TabButton, { label: tr(ui, "tab_debug", "Debug"), active: tab === "debug", onClick: () => setTab("debug") })
      ),
      React.createElement("div", { style: styles.content }, content)
    )
  );
}

function LauncherButton() {
  const visible = useValue(visible$);
  const payloadText = useValue(payload$);
  const data = React.useMemo(() => safeParsePayload(payloadText), [payloadText]);
  const ui = data.ui || {};
  const launcherOpen = typeof ui.launcher_open === "string" && ui.launcher_open.length > 0 ? ui.launcher_open : "MS2 OPEN";
  const launcherClosed = typeof ui.launcher_closed === "string" && ui.launcher_closed.length > 0 ? ui.launcher_closed : "MS2";
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
          onClick: () => safeTrigger("toggleVisible"),
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
        visible ? launcherOpen : launcherClosed
      )
    )
  );
}

const register = (moduleRegistry) => {
  moduleRegistry.append("GameTopRight", LauncherButton);
  moduleRegistry.append("Game", MultiplayerPanel);
};

export default register;
