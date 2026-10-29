(() => {
  // plugin-shims/preact-module.js
  var __p = globalThis.__preact;
  var h = __p.h;
  var Fragment = __p.Fragment;
  var createElement = __p.createElement;
  var render = __p.render;
  var createRef = __p.createRef;
  var isValidElement = __p.isValidElement;
  var Component = __p.Component;
  var cloneElement = __p.cloneElement;
  var createContext = __p.createContext;
  var toChildArray = __p.toChildArray;
  var options = __p.options;

  // plugin-shims/preact-hooks-module.js
  var __ph = globalThis.__preactHooks;
  var useState = __ph.useState;
  var useEffect = __ph.useEffect;
  var useCallback = __ph.useCallback;
  var useMemo = __ph.useMemo;
  var useRef = __ph.useRef;
  var useErrorBoundary = __ph.useErrorBoundary;
  var useReducer = __ph.useReducer;
  var useContext = __ph.useContext;
  var useLayoutEffect = __ph.useLayoutEffect;
  var useImperativeHandle = __ph.useImperativeHandle;
  var useDebugValue = __ph.useDebugValue;
  var useEventfulState = __ph.useEventfulState;

  // plugins/aichat/index.tsx
  try {
    chill.config.appGetOrCreate("AvatarAI", "", "AIChat \u89D2\u8272\uFF08AI\uFF09\u5934\u50CF\u56FE\u7247\u8DEF\u5F84\uFF0C\u7559\u7A7A\u663E\u793A\u9ED8\u8BA4\u5706\u5F62");
    chill.config.appGetOrCreate("AvatarUser", "", "AIChat \u73A9\u5BB6\u5934\u50CF\u56FE\u7247\u8DEF\u5F84\uFF0C\u7559\u7A7A\u663E\u793A\u9ED8\u8BA4\u5706\u5F62");
  } catch (_) {
  }
  var groups = {
    "0. \u540E\u7AEF": ["UseDshBridge", "DshBridgeUrl", "DshBridgeToken", "DshBridgeModel"],
    "1. LLM": ["Use_Ollama_API", "ThinkMode", "API_URL", "API_Key", "ModelName", "LogApiRequestBody", "FixApiPathForThinkMode"],
    "2. TTS": ["TTS_Service_URL", "TTS_Service_Script_Path", "LaunchTTSService", "QuitTTSServiceOnQuit", "Audio_File_Path", "AudioPathCheck", "Audio_File_Text", "PromptLang", "TargetLang", "JapaneseCheck", "VoiceVolume"],
    "3. UI": ["WindowWidth", "WindowHeightBase", "ReverseEnterBehavior", "BackgroundOpacity", "ShowWindowTitle"],
    "4. Persona": ["ExperimentalMemory", "SystemPrompt"],
    "Avatar": ["AvatarAI", "AvatarUser"]
  };
  var multilineKeys = /* @__PURE__ */ new Set(["Audio_File_Text", "SystemPrompt"]);
  var call = (name, ...args) => JSON.parse(chill.aichat[name](...args));
  var loadCfg = () => JSON.parse(chill.aichat.getAllConfig() || "{}");
  var loadDefaults = () => JSON.parse(chill.aichat.getAllConfigDefaults() || "{}");
  var fieldEstimatedHeight = (k) => !multilineKeys.has(k) ? 52 : k === "SystemPrompt" ? 320 : 210;
  var paginateFields = (fields, maxHeight) => {
    const pages = [];
    let current = [];
    let used = 0;
    const limit = Math.max(120, Math.floor(maxHeight));
    for (const k of fields) {
      const h2 = fieldEstimatedHeight(k);
      if (current.length > 0 && used + h2 > limit) {
        pages.push(current);
        current = [];
        used = 0;
      }
      current.push(k);
      used += h2;
    }
    if (current.length > 0)
      pages.push(current);
    return pages.length > 0 ? pages : [[]];
  };
  var SavePanel = ({ draft, defaults, setDraft, onCancel, onOk, status }) => {
    const pages = Object.keys(groups);
    const [page, setPage] = useState(0);
    const [contentPage, setContentPage] = useState(0);
    const [contentHeight, setContentHeight] = useState(220);
    const currentGroup = pages[page];
    const fields = groups[currentGroup] || [];
    const fieldPages = paginateFields(fields, contentHeight - (currentGroup === "4. Persona" ? 40 : 24));
    const maxContentPage = Math.max(0, fieldPages.length - 1);
    const displayFields = fieldPages[Math.min(contentPage, maxContentPage)] || [];
    useEffect(() => {
      setContentPage((p) => Math.min(p, maxContentPage));
    }, [maxContentPage, page]);
    const row = (k) => multilineKeys.has(k) ? /* @__PURE__ */ h("div", { key: k, style: { display: "Flex", flexDirection: "Column", marginBottom: 6, backgroundColor: "#11182799", paddingTop: 4, paddingBottom: 4, paddingLeft: 6, paddingRight: 6, borderWidth: 1, borderColor: "#1f2937", borderRadius: 6, flexShrink: 0 } }, /* @__PURE__ */ h("div", { style: { display: "Flex", flexDirection: "Row", justifyContent: "SpaceBetween", alignItems: "Center", marginBottom: 4 } }, /* @__PURE__ */ h("div", { style: { fontSize: 10, color: "#94a3b8" } }, k), /* @__PURE__ */ h(
      "div",
      {
        onPointerDown: () => setDraft({ ...draft, [k]: String(defaults[k] ?? "") }),
        style: { fontSize: 9, color: "#93c5fd", paddingLeft: 4, paddingRight: 4, paddingTop: 3, paddingBottom: 3, borderWidth: 1, borderColor: "#334155", borderRadius: 4 }
      },
      "\u6062\u590D"
    )), /* @__PURE__ */ h("div", { style: { height: 34, marginBottom: 4, backgroundColor: "#0b122099", borderWidth: 1, borderColor: "#1f2937", paddingLeft: 6, paddingRight: 6, paddingTop: 4, paddingBottom: 4, flexShrink: 0, overflow: "Hidden" } }, /* @__PURE__ */ h("div", { style: { fontSize: 9, color: "#64748b", whiteSpace: "Normal" } }, "\u9ED8\u8BA4: ", String(defaults[k] ?? ""))), /* @__PURE__ */ h(
      "textfield",
      {
        value: String(draft[k] ?? ""),
        multiline: true,
        "vertical-scroller-visibility": 1,
        onValueChanged: (e) => setDraft({ ...draft, [k]: e.newValue ?? "" }),
        style: { height: k === "SystemPrompt" ? 180 : 88, minHeight: k === "SystemPrompt" ? 180 : 88, maxHeight: k === "SystemPrompt" ? 180 : 88, width: "100%", fontSize: 10, backgroundColor: "#1f2937a6", borderWidth: 1, borderColor: "#334155", color: "#e2e8f0", paddingLeft: 6, paddingRight: 6, paddingTop: 4, paddingBottom: 4, flexShrink: 0 }
      }
    )) : /* @__PURE__ */ h("div", { key: k, style: { display: "Flex", flexDirection: "Row", alignItems: "Center", marginBottom: 6, backgroundColor: "#11182799", paddingTop: 4, paddingBottom: 4, paddingLeft: 6, paddingRight: 6, borderWidth: 1, borderColor: "#1f2937", borderRadius: 6, flexShrink: 0 } }, /* @__PURE__ */ h("div", { style: { width: 126, fontSize: 10, color: "#94a3b8", marginRight: 6 } }, k), /* @__PURE__ */ h(
      "textfield",
      {
        value: String(draft[k] ?? ""),
        onValueChanged: (e) => setDraft({ ...draft, [k]: e.newValue ?? "" }),
        style: { flexGrow: 1, height: 22, fontSize: 10, backgroundColor: "#1f2937a6", borderWidth: 1, borderColor: "#334155", color: "#e2e8f0", paddingLeft: 6, paddingRight: 6, marginRight: 6 }
      }
    ), /* @__PURE__ */ h("div", { style: { width: 120, fontSize: 9, color: "#64748b", overflow: "Hidden", marginRight: 6 } }, "\u9ED8\u8BA4: ", String(defaults[k] ?? "")), /* @__PURE__ */ h(
      "div",
      {
        onPointerDown: () => setDraft({ ...draft, [k]: String(defaults[k] ?? "") }),
        style: { fontSize: 9, color: "#93c5fd", paddingLeft: 4, paddingRight: 4, paddingTop: 3, paddingBottom: 3, borderWidth: 1, borderColor: "#334155", borderRadius: 4 }
      },
      "\u6062\u590D"
    ));
    return /* @__PURE__ */ h("div", { style: { flexGrow: 1, height: "100%", display: "Flex", flexDirection: "Column", backgroundColor: "transparent", padding: 10, overflow: "Hidden" } }, /* @__PURE__ */ h("div", { style: { fontSize: 13, color: "#e2e8f0", marginBottom: 2, flexShrink: 0 } }, "AIChat \u914D\u7F6E"), /* @__PURE__ */ h("div", { style: { fontSize: 9, color: "#64748b", marginBottom: 8, flexShrink: 0, whiteSpace: "Normal" } }, status?.available ? "\u5DF2\u8FDE\u63A5" : "\u672A\u5B89\u88C5", "\u3000busy: ", String(!!status?.isBusy), "\u3000ready: ", String(!!status?.isReady), "\u3000ver: ", status?.apiVersion || "-"), /* @__PURE__ */ h("div", { style: { display: "Flex", flexDirection: "Row", marginBottom: 8, flexShrink: 0 } }, pages.map((g, idx) => /* @__PURE__ */ h("div", { key: g, onPointerDown: () => {
      setPage(idx);
      setContentPage(0);
    }, style: { fontSize: 10, color: idx === page ? "#0f172a" : "#cbd5e1", backgroundColor: idx === page ? "#93c5fd" : "#1e293b", paddingLeft: 8, paddingRight: 8, paddingTop: 5, paddingBottom: 5, borderRadius: 6, marginRight: 6 } }, g.replace(/^\d+\.\s*/, "")))), /* @__PURE__ */ h("div", { style: { fontSize: 11, color: "#93c5fd", marginBottom: 6, flexShrink: 0 } }, currentGroup), /* @__PURE__ */ h(
      "div",
      {
        style: { flexGrow: 1, flexShrink: 1, minHeight: 120, backgroundColor: "#0b122099", borderWidth: 1, borderColor: "#1f2937", borderRadius: 6, padding: 6, overflow: "Hidden" },
        onGeometryChanged: (e) => setContentHeight(Math.max(120, Math.floor(e?.newRect?.height ?? e?.target?.layout?.height ?? 220)))
      },
      displayFields.map(row),
      currentGroup === "4. Persona" && /* @__PURE__ */ h("div", { style: { fontSize: 9, color: "#64748b", marginTop: 4, flexShrink: 0 } }, "SystemPrompt \u5EFA\u8BAE\u624B\u52A8\u7C98\u8D34\u5B8C\u6574\u5185\u5BB9\u540E\u518D\u70B9\u51FB\u786E\u5B9A\u4FDD\u5B58\u3002"),
      "      ",
      currentGroup === "Avatar" && /* @__PURE__ */ h("div", { style: { display: "Flex", flexDirection: "Column", marginTop: 6, flexShrink: 0 } }, /* @__PURE__ */ h("div", { style: { display: "Flex", flexDirection: "Row", alignItems: "Center", marginBottom: 6 } }, /* @__PURE__ */ h(Avatar, { role: "ai", size: 44, path: String(draft["AvatarAI"] ?? "") }), /* @__PURE__ */ h("div", { style: { marginLeft: 10, fontSize: 9, color: "#64748b", whiteSpace: "Normal" } }, "\u89D2\u8272\u5934\u50CF\u9884\u89C8\uFF08AI\uFF09")), /* @__PURE__ */ h("div", { style: { display: "Flex", flexDirection: "Row", alignItems: "Center", marginBottom: 6 } }, /* @__PURE__ */ h(Avatar, { role: "user", size: 44, path: String(draft["AvatarUser"] ?? "") }), /* @__PURE__ */ h("div", { style: { marginLeft: 10, fontSize: 9, color: "#64748b", whiteSpace: "Normal" } }, "\u6211\u7684\u5934\u50CF\u9884\u89C8")), /* @__PURE__ */ h("div", { style: { fontSize: 9, color: "#64748b", whiteSpace: "Normal", marginBottom: 6 } }, "\u586B\u672C\u5730\u56FE\u7247\u5B8C\u6574\u8DEF\u5F84\uFF08png / jpg\uFF09\uFF0C\u7559\u7A7A\u5219\u663E\u793A\u9ED8\u8BA4\u5706\u5F62\u3002\u70B9\u300C\u6062\u590D\u300D\u53EF\u6E05\u7A7A\u3002"))
    ), /* @__PURE__ */ h("div", { style: { display: "Flex", flexDirection: "Row", justifyContent: "SpaceBetween", marginTop: 8, flexShrink: 0 } }, /* @__PURE__ */ h("div", { onPointerDown: () => setContentPage(Math.max(0, contentPage - 1)), style: { fontSize: 10, color: contentPage > 0 ? "#cbd5e1" : "#475569", padding: "5 8", borderWidth: 1, borderColor: "#334155", borderRadius: 6 } }, "\u4E0A\u4E00\u9875"), /* @__PURE__ */ h("div", { style: { fontSize: 10, color: "#64748b" } }, `${Math.min(contentPage, maxContentPage) + 1}/${fieldPages.length}`), /* @__PURE__ */ h("div", { onPointerDown: () => setContentPage(Math.min(maxContentPage, contentPage + 1)), style: { fontSize: 10, color: contentPage < maxContentPage ? "#cbd5e1" : "#475569", padding: "5 8", borderWidth: 1, borderColor: "#334155", borderRadius: 6 } }, "\u4E0B\u4E00\u9875")), /* @__PURE__ */ h("div", { style: { display: "Flex", flexDirection: "Row", justifyContent: "FlexEnd", marginTop: 8, flexShrink: 0 } }, /* @__PURE__ */ h("div", { onPointerDown: onCancel, style: { fontSize: 11, color: "#cbd5e1", padding: "6 12", marginRight: 8, borderWidth: 1, borderColor: "#475569", borderRadius: 6 } }, "\u53D6\u6D88"), /* @__PURE__ */ h("div", { onPointerDown: onOk, style: { fontSize: 11, color: "#0f172a", backgroundColor: "#93c5fd", padding: "6 12", borderRadius: 6 } }, "\u786E\u5B9A")));
  };
  var HISTORY_FILE = "window-states/aichat-history.json";
  var MAX_MSGS = 200;
  var splitSentences = (text) => {
    const src = String(text || "").replace(/\r/g, "");
    const out = [];
    let buf = "";
    for (let i = 0; i < src.length; i++) {
      const ch = src.charAt(i);
      if (ch === "\n") {
        const t = buf.trim();
        if (t)
          out.push(t);
        buf = "";
        continue;
      }
      buf += ch;
      if ("\u3002\uFF01\uFF1F!?\u2026".indexOf(ch) >= 0) {
        const t = buf.trim();
        if (t)
          out.push(t);
        buf = "";
      }
    }
    const tail = buf.trim();
    if (tail)
      out.push(tail);
    return out.length > 0 ? out : src.trim() ? [src.trim()] : [];
  };
  var loadHistory = () => {
    try {
      if (!chill.io.exists(HISTORY_FILE))
        return [];
      const arr = JSON.parse(chill.io.readText(HISTORY_FILE) || "[]");
      if (!Array.isArray(arr))
        return [];
      return arr.filter((m) => m && typeof m.text === "string" && m.text.length > 0).map((m, i) => ({
        id: Number(m.id) || Date.now() + i,
        role: m.role === "user" ? "user" : "ai",
        text: String(m.text)
      }));
    } catch (_) {
      return [];
    }
  };
  var saveHistory = (msgs) => {
    try {
      chill.io.writeText(HISTORY_FILE, JSON.stringify(msgs.slice(-MAX_MSGS)));
    } catch (_) {
    }
  };
  var Avatar = ({ role, size, path }) => {
    const src = String(path || "").trim();
    return /* @__PURE__ */ h(
      "div",
      {
        style: {
          width: size,
          height: size,
          borderRadius: size / 2,
          backgroundColor: role === "user" ? "#4f46e5" : "#0284c7",
          display: "Flex",
          alignItems: "Center",
          justifyContent: "Center",
          fontSize: Math.round(size * 0.46),
          color: "#ffffff",
          flexShrink: 0,
          marginRight: role === "ai" ? 6 : 0,
          marginLeft: role === "user" ? 6 : 0,
          position: "Relative",
          overflow: "Hidden"
        }
      },
      /* @__PURE__ */ h("div", { style: { position: "Absolute" } }, role === "user" ? "\u6211" : "\u732B"),
      src.length > 0 ? /* @__PURE__ */ h("img", { src, style: { position: "Absolute", width: "100%", height: "100%" } }) : null
    );
  };
  var Bubble = ({ m, maxW, avatar, aiPath, userPath }) => /* @__PURE__ */ h(
    "div",
    {
      style: {
        display: "Flex",
        flexDirection: "Row",
        justifyContent: m.role === "user" ? "FlexEnd" : "FlexStart",
        marginBottom: 6,
        flexShrink: 0
      }
    },
    m.role === "ai" ? /* @__PURE__ */ h(Avatar, { role: "ai", size: avatar, path: aiPath }) : null,
    /* @__PURE__ */ h(
      "div",
      {
        style: {
          maxWidth: maxW,
          backgroundColor: m.role === "user" ? "#3b82f6e6" : "#1f2937e6",
          color: "#f1f5f9",
          fontSize: 12,
          paddingLeft: 8,
          paddingRight: 8,
          paddingTop: 6,
          paddingBottom: 6,
          borderRadius: 10,
          whiteSpace: "Normal"
        }
      },
      m.text
    ),
    m.role === "user" ? /* @__PURE__ */ h(Avatar, { role: "user", size: avatar, path: userPath }) : null
  );
  var ChatPanel = ({ compact = false }) => {
    const [prompt, setPrompt] = useState("");
    const fieldRef = useRef(null);
    const [rec, setRec] = useState(false);
    const [cfgMode, setCfgMode] = useState(false);
    const [draft, setDraft] = useState({});
    const [defaults, setDefaults] = useState({});
    const [status, setStatus] = useState({ available: false });
    const [busy, setBusy] = useState(false);
    const [msgs, setMsgs] = useState(() => loadHistory());
    const [panelW, setPanelW] = useState(420);
    const [panelH, setPanelH] = useState(360);
    const [avatarRev, setAvatarRev] = useState(0);
    const [viewH, setViewH] = useState(0);
    const [contentH, setContentH] = useState(0);
    const [scrollY, setScrollY] = useState(0);
    const heightsRef = useRef(/* @__PURE__ */ new Map());
    const [renderRev, setRenderRev] = useState(0);
    const [dragging, setDragging] = useState(false);
    const [vu, setVu] = useState(0);
    const followRef = useRef(true);
    const segCountRef = useRef(0);
    const dragRef = useRef({ active: false, startY: 0, startScroll: 0 });
    const maxScroll = Math.max(0, contentH - viewH);
    const clampY = (v) => Math.max(0, Math.min(maxScroll, v));
    useEffect(() => {
      if (!rec) {
        setVu(0);
        return;
      }
      const id = setInterval(() => {
        const t = Date.now() / 160;
        const noise = Math.random() * 0.5;
        const level = Math.max(0.08, Math.min(1, Math.abs(Math.sin(t)) * 0.7 + noise * 0.3));
        setVu(level);
      }, 80);
      return () => clearInterval(id);
    }, [rec]);
    useEffect(() => {
      const id = setInterval(() => {
        try {
          const s = JSON.parse(chill.aichat.getStatus());
          setStatus(s);
          setBusy(!!s.isBusy);
        } catch (_) {
        }
      }, 800);
      return () => clearInterval(id);
    }, []);
    const vuStyle = useMemo(() => ({
      width: compact ? 14 : 18,
      height: compact ? 14 : 18,
      borderRadius: 999,
      backgroundColor: rec ? "#f87171" : "#64748b",
      scale: rec ? 0.9 + vu * 0.9 : 1,
      opacity: rec ? 0.5 + vu * 0.5 : 0.35,
      marginRight: 6,
      borderWidth: 1,
      borderColor: rec ? "#fecaca" : "#94a3b8"
    }), [rec, vu, compact]);
    const appendMsgs = (items) => {
      setMsgs((prev) => {
        const next = [...prev, ...items].slice(-MAX_MSGS);
        saveHistory(next);
        return next;
      });
      followRef.current = true;
    };
    useEffect(() => {
      setStatus(JSON.parse(chill.aichat.getStatus()));
      const cfg = loadCfg();
      const defs = loadDefaults();
      try {
        cfg.AvatarAI = String(chill.config.appGet("AvatarAI") ?? "");
        cfg.AvatarUser = String(chill.config.appGet("AvatarUser") ?? "");
      } catch (_) {
      }
      defs.AvatarAI = "";
      defs.AvatarUser = "";
      setDraft(cfg);
      setDefaults(defs);
      const tSeg = typeof chill.aichat.onSegmentShown === "function" ? chill.aichat.onSegmentShown((text) => {
        const zh = String(text || "").trim();
        if (!zh)
          return;
        segCountRef.current++;
        appendMsgs([{ id: Date.now(), role: "ai", text: zh }]);
      }) : "";
      const t = chill.aichat.onConversationCompleted((json) => {
        try {
          if (segCountRef.current > 0) {
            segCountRef.current = 0;
            return;
          }
          const d = JSON.parse(json || "{}");
          const zh = String(d.SubtitleText || "").trim();
          if (!zh)
            return;
          const parts = splitSentences(zh);
          if (parts.length === 0)
            return;
          const now = Date.now();
          appendMsgs(parts.map((p, i) => ({ id: now + i, role: "ai", text: p })));
        } catch (_) {
        }
      });
      return () => {
        if (t)
          chill.aichat.offConversationCompleted(t);
        if (tSeg && typeof chill.aichat.offSegmentShown === "function")
          chill.aichat.offSegmentShown(tSeg);
      };
    }, []);
    useEffect(() => {
      if (followRef.current)
        setScrollY(clampY(contentH - viewH));
    }, [contentH, viewH, msgs.length]);
    const doSend = (text) => {
      const f = fieldRef.current;
      const ve = f?.ve ?? f;
      const r = call("startTextConversation", text, "wm-plugin");
      if (r.ok) {
        segCountRef.current = 0;
        appendMsgs([{ id: Date.now(), role: "user", text: text.trim() }]);
        if (ve) {
          if (ve.SetValueWithoutNotify)
            ve.SetValueWithoutNotify("");
          else
            ve.value = "";
        }
        setPrompt("");
        if (f)
          setTimeout(() => {
            if (fieldRef.current !== f)
              return;
            try {
              if (ve?.Focus)
                ve.Focus();
              else if (f.focus)
                f.focus();
            } catch (_) {
            }
          }, 0);
      }
    };
    const clearInput = () => {
      const f = fieldRef.current;
      const ve = f?.ve ?? f;
      if (ve) {
        if (ve.SetValueWithoutNotify)
          ve.SetValueWithoutNotify("");
        else
          ve.value = "";
      }
      setPrompt("");
    };
    const send = () => {
      const f = fieldRef.current;
      const ve = f?.ve ?? f;
      const text = String(ve?.value ?? prompt ?? "");
      if (!text.trim())
        return;
      if (busy) {
        let interrupted = false;
        try {
          if (typeof chill.aichat.interrupt === "function") {
            const r = JSON.parse(chill.aichat.interrupt() || "{}");
            interrupted = !!r.ok;
          }
        } catch (_) {
        }
        clearInput();
        setTimeout(() => {
          setBusy(false);
          doSend(text);
        }, interrupted ? 180 : 60);
        return;
      }
      doSend(text);
    };
    const micDown = () => {
      const r = call("startVoiceCapture");
      if (r.ok)
        setRec(true);
    };
    const micUp = () => {
      const r = call("stopVoiceCaptureAndSend", "wm-plugin");
      setRec(false);
      if (!r.ok)
        console.log(r.error);
    };
    const save = () => {
      let ok = true;
      for (const k of Object.keys(draft)) {
        if (k === "AvatarAI" || k === "AvatarUser") {
          try {
            chill.config.appSet(k, String(draft[k] ?? ""));
          } catch (_) {
            ok = false;
          }
          continue;
        }
        const r = call("setConfig", k, String(draft[k] ?? ""));
        if (!r.ok)
          ok = false;
      }
      const rs = call("saveConfig");
      setCfgMode(false);
      setAvatarRev((v) => v + 1);
      setStatus(JSON.parse(chill.aichat.getStatus()));
      if (!ok || !rs.ok)
        console.log("\u4FDD\u5B58\u5931\u8D25");
    };
    const onScrubDown = (e) => {
      followRef.current = false;
      dragRef.current = { active: true, startY: e?.position?.y ?? 0, startScroll: scrollY };
      setDragging(true);
    };
    const onScrubMove = (e) => {
      const d = dragRef.current;
      if (!d.active)
        return;
      const dy = (e?.position?.y ?? 0) - d.startY;
      setScrollY(clampY(d.startScroll - dy));
    };
    const onScrubUp = () => {
      dragRef.current.active = false;
      setDragging(false);
    };
    const jumpToLatest = () => {
      followRef.current = true;
      setScrollY(maxScroll);
    };
    const aiAvatarPath = useMemo(() => {
      try {
        return String(chill.config.appGet("AvatarAI") ?? "");
      } catch (_) {
        return "";
      }
    }, [avatarRev]);
    const userAvatarPath = useMemo(() => {
      try {
        return String(chill.config.appGet("AvatarUser") ?? "");
      } catch (_) {
        return "";
      }
    }, [avatarRev]);
    const availW = Math.max(140, panelW - 26);
    const sendW = Math.min(64, Math.max(46, Math.round(availW * 0.15)));
    const textW = Math.max(80, availW - sendW - 8);
    const usableTextW = Math.max(40, textW - 18);
    const measurePx = (s) => {
      let w = 0;
      for (const ch of s) {
        const cp = ch.codePointAt(0) ?? 0;
        w += cp > 11904 ? 12.6 : 6.7;
      }
      return w;
    };
    const wrapRows = (s) => {
      let rows = 0;
      for (const part of s.split("\n"))
        rows += Math.max(1, Math.ceil(measurePx(part) / usableTextW));
      return Math.max(1, rows);
    };
    const lineCount = useMemo(() => wrapRows(prompt.length > 0 ? prompt : " "), [prompt, usableTextW]);
    const caretIndex = (() => {
      try {
        const ve = fieldRef.current?.ve ?? fieldRef.current;
        const c = ve?.cursorIndex;
        return typeof c === "number" && c >= 0 ? c : prompt.length;
      } catch (_) {
        return prompt.length;
      }
    })();
    const caretRow = Math.max(0, wrapRows(prompt.slice(0, Math.max(0, caretIndex))) - 1);
    const lineH = 17;
    const fullInputH = Math.max(32, 12 + lineCount * lineH);
    const inputMaxH = compact ? 64 : Math.max(70, Math.min(190, Math.round(panelH * 0.34)));
    const inputH = Math.min(fullInputH, inputMaxH);
    const inputOverflow = Math.max(0, fullInputH - inputH);
    const inputScroll = Math.max(0, Math.min(inputOverflow, caretRow * lineH + 6 - (inputH - lineH - 6)));
    const RENDER_OVERSCAN = 200;
    const heightOf = (id) => heightsRef.current.get(id) ?? 58;
    const bubbleLayout = useMemo(() => {
      const out = new Array(msgs.length);
      let acc = 0;
      for (let i = 0; i < msgs.length; i++) {
        out[i] = acc;
        acc += heightOf(msgs[i].id);
      }
      return { offsets: out, total: acc };
    }, [msgs, renderRev]);
    const visibleRange = useMemo(() => {
      const len = msgs.length;
      if (len === 0)
        return { first: 0, last: -1, top: 0, bottom: 0 };
      const y0 = clampY(scrollY) - RENDER_OVERSCAN;
      const y1 = clampY(scrollY) + Math.max(viewH, 60) + RENDER_OVERSCAN;
      let first = 0;
      while (first < len - 1 && bubbleLayout.offsets[first] + heightOf(msgs[first].id) < y0)
        first++;
      let last = first;
      while (last < len - 1 && bubbleLayout.offsets[last] < y1)
        last++;
      const endOffset = bubbleLayout.offsets[last] + heightOf(msgs[last].id);
      return {
        first,
        last,
        top: bubbleLayout.offsets[first],
        bottom: Math.max(0, bubbleLayout.total - endOffset)
      };
    }, [msgs, renderRev, scrollY, viewH, bubbleLayout]);
    const measureBubble = (id, raw) => {
      if (!(raw > 0))
        return;
      const h2 = Math.round(raw);
      const prev = heightsRef.current.get(id);
      if (prev === h2)
        return;
      heightsRef.current.set(id, h2);
      setRenderRev((v) => v + 1);
    };
    const thumbH = maxScroll > 0 ? Math.max(22, Math.round(viewH * Math.min(1, viewH / Math.max(contentH, 1)))) : 0;
    const thumbTop = maxScroll > 0 ? Math.round(clampY(scrollY) / maxScroll * Math.max(0, viewH - thumbH)) : 0;
    if (cfgMode && !compact)
      return /* @__PURE__ */ h(SavePanel, { draft, defaults, setDraft, onCancel: () => setCfgMode(false), onOk: save, status });
    return /* @__PURE__ */ h(
      "div",
      {
        onGeometryChanged: (e) => {
          const rr = e?.newRect;
          if (rr) {
            if (rr.width)
              setPanelW(Math.max(220, Math.floor(rr.width)));
            if (rr.height)
              setPanelH(Math.max(160, Math.floor(rr.height)));
          }
        },
        style: { flexGrow: 1, display: "Flex", flexDirection: "Column", backgroundColor: "transparent", padding: 10, overflow: "Hidden" }
      },
      !compact && !status.available && /* @__PURE__ */ h("div", { style: { fontSize: 11, color: "#fca5a5", marginBottom: 6, flexShrink: 0 } }, "AIChat \u672A\u5B89\u88C5"),
      !compact && /* @__PURE__ */ h(
        "div",
        {
          onGeometryChanged: (e) => {
            const hh = e?.newRect?.height;
            if (hh)
              setViewH(Math.max(1, Math.floor(hh)));
          },
          style: { flexGrow: 1, flexShrink: 1, minHeight: 60, position: "Relative", overflow: "Hidden", marginBottom: 6 }
        },
        /* @__PURE__ */ h(
          "div",
          {
            onGeometryChanged: (e) => {
              const hh = e?.newRect?.height;
              if (hh)
                setContentH(Math.max(1, Math.floor(hh)));
            },
            style: { position: "Absolute", left: 0, right: 0, top: 0, display: "Flex", flexDirection: "Column", translate: `0 ${-Math.round(clampY(scrollY))}px` }
          },
          msgs.length === 0 ? /* @__PURE__ */ h("div", { style: { fontSize: 11, color: "#94a3b8" } }, "\uFF08\u8FD8\u6CA1\u6709\u5BF9\u8BDD\uFF09") : null,
          visibleRange.top > 0 ? /* @__PURE__ */ h("div", { style: { height: Math.round(visibleRange.top) } }) : null,
          visibleRange.last >= visibleRange.first && msgs.slice(visibleRange.first, visibleRange.last + 1).map((m) => /* @__PURE__ */ h("div", { key: m.id, onGeometryChanged: (e) => {
            const hh = e?.newRect?.height;
            if (hh)
              measureBubble(m.id, hh);
          } }, /* @__PURE__ */ h(Bubble, { m, maxW: Math.max(120, Math.round(panelW * 0.6)), avatar: 26, aiPath: aiAvatarPath, userPath: userAvatarPath }))),
          visibleRange.bottom > 0 ? /* @__PURE__ */ h("div", { style: { height: Math.round(visibleRange.bottom) } }) : null
        ),
        /* @__PURE__ */ h("div", { onPointerDown: onScrubDown, style: { position: "Absolute", left: 0, top: 0, right: 0, bottom: 0 } }),
        maxScroll > 0 ? /* @__PURE__ */ h("div", { style: { position: "Absolute", right: 0, top: 0, width: 8, height: "100%", backgroundColor: "#ffffff12", borderRadius: 4 } }, /* @__PURE__ */ h(
          "div",
          {
            onPointerDown: onScrubDown,
            style: { position: "Absolute", left: 1, right: 1, top: thumbTop, height: thumbH, backgroundColor: "#94a3b8cc", borderRadius: 3 }
          }
        )) : null,
        scrollY < maxScroll - 2 ? /* @__PURE__ */ h(
          "div",
          {
            onPointerDown: jumpToLatest,
            style: { position: "Absolute", right: 14, bottom: 6, width: 22, height: 22, borderRadius: 11, backgroundColor: "#1e293be6", borderWidth: 1, borderColor: "#475569", display: "Flex", alignItems: "Center", justifyContent: "Center", fontSize: 11, color: "#cbd5e1" }
          },
          "\u2193"
        ) : null,
        dragging ? /* @__PURE__ */ h("div", { onPointerMove: onScrubMove, onPointerUp: onScrubUp, style: { position: "Absolute", left: 0, top: 0, right: 0, bottom: 0 } }) : null
      ),
      /* @__PURE__ */ h("div", { style: { display: "Flex", flexDirection: "Row", alignItems: "FlexEnd", flexShrink: 0 } }, /* @__PURE__ */ h("div", { style: { width: textW, height: inputH, overflow: "Hidden", position: "Relative", backgroundColor: "#1f2937a6", borderWidth: 1, borderColor: "#334155", borderRadius: 4 } }, /* @__PURE__ */ h(
        "textfield",
        {
          value: prompt,
          ref: (el) => {
            fieldRef.current = el;
          },
          onValueChanged: (e) => setPrompt(e.newValue ?? ""),
          multiline: true,
          onKeyDown: (e) => {
            const k = e.keyCode ?? e.key;
            if (k === 13 || k === 10 || k === "Enter" || k === "NumpadEnter") {
              if (e.shiftKey)
                return;
              if (e.PreventDefault)
                e.PreventDefault();
              if (e.StopPropagation)
                e.StopPropagation();
              send();
            }
          },
          style: { position: "Absolute", left: 0, top: 0, width: textW, height: fullInputH, translate: `0 ${-Math.round(inputScroll)}px`, fontSize: 12, backgroundColor: "#00000000", borderWidth: 0, color: "#e2e8f0", paddingLeft: 8, paddingRight: 8, paddingTop: 6, paddingBottom: 6 }
        }
      )), /* @__PURE__ */ h(
        "div",
        {
          onPointerDown: send,
          style: { width: sendW, height: inputH, marginLeft: 6, backgroundColor: busy ? "#f59e0b" : "#3b82f6", borderRadius: 6, display: "Flex", alignItems: "Center", justifyContent: "Center", fontSize: busy ? 11 : 12, color: "#ffffff" }
        },
        busy ? "\u6253\u65AD" : "\u53D1\u9001"
      )),
      /* @__PURE__ */ h("div", { style: { display: "Flex", flexDirection: "Row", marginTop: 6, flexShrink: 0 } }, /* @__PURE__ */ h(
        "div",
        {
          onPointerDown: micDown,
          onPointerUp: micUp,
          onPointerLeave: micUp,
          style: { fontSize: 11, color: rec ? "#fff" : "#e2e8f0", backgroundColor: rec ? "#ef4444" : "#334155", paddingLeft: 8, paddingRight: 8, paddingTop: 5, paddingBottom: 5, borderRadius: 6, marginRight: 6, display: "Flex", flexDirection: "Row", alignItems: "Center" }
        },
        /* @__PURE__ */ h("div", { style: vuStyle }),
        rec ? "\u677E\u5F00\u53D1\u9001" : "\u6309\u4F4F\u8BF4\u8BDD"
      ), !compact && /* @__PURE__ */ h("div", { onPointerDown: () => setCfgMode(true), style: { fontSize: 11, color: "#e2e8f0", backgroundColor: "#334155", paddingLeft: 8, paddingRight: 8, paddingTop: 5, paddingBottom: 5, borderRadius: 6 } }, "\u914D\u7F6E"))
    );
  };
  __registerPlugin({
    id: "aichat",
    title: "AIChat",
    width: 520,
    height: 460,
    initialX: 120,
    initialY: 80,
    resizable: true,
    compact: { width: 420, height: 120, component: () => /* @__PURE__ */ h(ChatPanel, { compact: true }) },
    launcher: {
      text: "",
      background: "#008055"
    },
    component: () => /* @__PURE__ */ h(ChatPanel, null)
  });
})();
//# sourceMappingURL=app.js.map
