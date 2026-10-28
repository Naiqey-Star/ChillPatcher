import { h, Fragment } from "preact"
import { useEffect, useMemo, useRef, useState } from "preact/hooks"

declare const __registerPlugin: any
declare const chill: any

type KV = Record<string, string>
const groups: Record<string, string[]> = {
  "1. LLM": ["Use_Ollama_API","ThinkMode","API_URL","API_Key","ModelName","LogApiRequestBody","FixApiPathForThinkMode"],
  "2. TTS": ["TTS_Service_URL","TTS_Service_Script_Path","LaunchTTSService","QuitTTSServiceOnQuit","Audio_File_Path","AudioPathCheck","Audio_File_Text","PromptLang","TargetLang","JapaneseCheck","VoiceVolume"],
  "3. UI": ["WindowWidth","WindowHeightBase","ReverseEnterBehavior","BackgroundOpacity","ShowWindowTitle"],
  "4. Persona": ["ExperimentalMemory","SystemPrompt"],
}
const multilineKeys = new Set(["Audio_File_Text", "SystemPrompt"])

const call = (name: string, ...args: any[]) => JSON.parse(chill.aichat[name](...args))
const loadCfg = (): KV => JSON.parse(chill.aichat.getAllConfig() || "{}")
const loadDefaults = (): KV => JSON.parse(chill.aichat.getAllConfigDefaults() || "{}")

const fieldEstimatedHeight = (k:string) => !multilineKeys.has(k) ? 52 : (k === "SystemPrompt" ? 320 : 210)

const paginateFields = (fields:string[], maxHeight:number) => {
  const pages:string[][] = []
  let current:string[] = []
  let used = 0
  const limit = Math.max(120, Math.floor(maxHeight))

  for (const k of fields) {
    const h = fieldEstimatedHeight(k)
    if (current.length > 0 && used + h > limit) {
      pages.push(current)
      current = []
      used = 0
    }
    current.push(k)
    used += h
  }

  if (current.length > 0) pages.push(current)
  return pages.length > 0 ? pages : [[]]
}

const SavePanel = ({draft,defaults,setDraft,onCancel,onOk}:{draft:KV;defaults:KV;setDraft:(v:KV)=>void;onCancel:()=>void;onOk:()=>void}) => {
  const pages = Object.keys(groups)
  const [page,setPage] = useState(0)
  const [contentPage,setContentPage] = useState(0)
  const [contentHeight,setContentHeight] = useState(220)
  const currentGroup = pages[page]
  const fields = groups[currentGroup] || []
  const fieldPages = paginateFields(fields, contentHeight - (currentGroup === "4. Persona" ? 40 : 24))
  const maxContentPage = Math.max(0, fieldPages.length - 1)
  const displayFields = fieldPages[Math.min(contentPage, maxContentPage)] || []

  useEffect(()=>{
    setContentPage(p=>Math.min(p, maxContentPage))
  },[maxContentPage, page])

  const row = (k:string)=>(
    multilineKeys.has(k)
      ? <div key={k} style={{display:"Flex",flexDirection:"Column",marginBottom:6,backgroundColor:"#11182799",paddingTop:4,paddingBottom:4,paddingLeft:6,paddingRight:6,borderWidth:1,borderColor:"#1f2937",borderRadius:6,flexShrink:0}}>
          <div style={{display:"Flex",flexDirection:"Row",justifyContent:"SpaceBetween",alignItems:"Center",marginBottom:4}}>
            <div style={{fontSize:10,color:"#94a3b8"}}>{k}</div>
            <div onPointerDown={()=>setDraft({...draft,[k]:String(defaults[k] ?? "")})}
              style={{fontSize:9,color:"#93c5fd",paddingLeft:4,paddingRight:4,paddingTop:3,paddingBottom:3,borderWidth:1,borderColor:"#334155",borderRadius:4}}>恢复</div>
          </div>
          <div style={{height:34,marginBottom:4,backgroundColor:"#0b122099",borderWidth:1,borderColor:"#1f2937",paddingLeft:6,paddingRight:6,paddingTop:4,paddingBottom:4,flexShrink:0,overflow:"Hidden"}}>
            <div style={{fontSize:9,color:"#64748b",whiteSpace:"Normal"}}>默认: {String(defaults[k] ?? "")}</div>
          </div>
          <textfield value={String(draft[k] ?? "")} multiline={true} vertical-scroller-visibility={1} onValueChanged={(e:any)=>setDraft({...draft,[k]:e.newValue ?? ""})}
            style={{height:k==="SystemPrompt"?180:88,minHeight:k==="SystemPrompt"?180:88,maxHeight:k==="SystemPrompt"?180:88,width:"100%",fontSize:10,backgroundColor:"#1f2937a6",borderWidth:1,borderColor:"#334155",color:"#e2e8f0",paddingLeft:6,paddingRight:6,paddingTop:4,paddingBottom:4,flexShrink:0}} />
        </div>
      : <div key={k} style={{display:"Flex",flexDirection:"Row",alignItems:"Center",marginBottom:6,backgroundColor:"#11182799",paddingTop:4,paddingBottom:4,paddingLeft:6,paddingRight:6,borderWidth:1,borderColor:"#1f2937",borderRadius:6,flexShrink:0}}>
          <div style={{width:126,fontSize:10,color:"#94a3b8",marginRight:6}}>{k}</div>
          <textfield value={String(draft[k] ?? "")} onValueChanged={(e:any)=>setDraft({...draft,[k]:e.newValue ?? ""})}
            style={{flexGrow:1,height:22,fontSize:10,backgroundColor:"#1f2937a6",borderWidth:1,borderColor:"#334155",color:"#e2e8f0",paddingLeft:6,paddingRight:6,marginRight:6}}/>
          <div style={{width:120,fontSize:9,color:"#64748b",overflow:"Hidden",marginRight:6}}>默认: {String(defaults[k] ?? "")}</div>
          <div onPointerDown={()=>setDraft({...draft,[k]:String(defaults[k] ?? "")})}
            style={{fontSize:9,color:"#93c5fd",paddingLeft:4,paddingRight:4,paddingTop:3,paddingBottom:3,borderWidth:1,borderColor:"#334155",borderRadius:4}}>恢复</div>
        </div>
  )

  return <div style={{flexGrow:1,height:"100%",display:"Flex",flexDirection:"Column",backgroundColor:"transparent",padding:10,overflow:"Hidden"}}>
    <div style={{fontSize:13,color:"#e2e8f0",marginBottom:8,flexShrink:0}}>AIChat 配置</div>

    <div style={{display:"Flex",flexDirection:"Row",marginBottom:8,flexShrink:0}}>
      {pages.map((g,idx)=><div key={g} onPointerDown={()=>{ setPage(idx); setContentPage(0) }} style={{fontSize:10,color:idx===page?"#0f172a":"#cbd5e1",backgroundColor:idx===page?"#93c5fd":"#1e293b",paddingLeft:8,paddingRight:8,paddingTop:5,paddingBottom:5,borderRadius:6,marginRight:6}}>{g.replace("1. ","").replace("2. ","").replace("3. ","").replace("4. ","")}</div>)}
    </div>

    <div style={{fontSize:11,color:"#93c5fd",marginBottom:6,flexShrink:0}}>{currentGroup}</div>
    <div style={{flexGrow:1,flexShrink:1,minHeight:120,backgroundColor:"#0b122099",borderWidth:1,borderColor:"#1f2937",borderRadius:6,padding:6,overflow:"Hidden"}}
      onGeometryChanged={(e:any)=>setContentHeight(Math.max(120, Math.floor(e?.newRect?.height ?? e?.target?.layout?.height ?? 220)))}>
      {displayFields.map(row)}
      {currentGroup === "4. Persona" && <div style={{fontSize:9,color:"#64748b",marginTop:4,flexShrink:0}}>SystemPrompt 建议手动粘贴完整内容后再点击确定保存。</div>}
    </div>

    <div style={{display:"Flex",flexDirection:"Row",justifyContent:"SpaceBetween",marginTop:8,flexShrink:0}}>
      <div onPointerDown={()=>setContentPage(Math.max(0,contentPage-1))} style={{fontSize:10,color:contentPage>0?"#cbd5e1":"#475569",padding:"5 8",borderWidth:1,borderColor:"#334155",borderRadius:6}}>上一页</div>
      <div style={{fontSize:10,color:"#64748b"}}>{`${Math.min(contentPage,maxContentPage)+1}/${fieldPages.length}`}</div>
      <div onPointerDown={()=>setContentPage(Math.min(maxContentPage,contentPage+1))} style={{fontSize:10,color:contentPage<maxContentPage?"#cbd5e1":"#475569",padding:"5 8",borderWidth:1,borderColor:"#334155",borderRadius:6}}>下一页</div>
    </div>

    <div style={{display:"Flex",flexDirection:"Row",justifyContent:"FlexEnd",marginTop:8,flexShrink:0}}>
      <div onPointerDown={onCancel} style={{fontSize:11,color:"#cbd5e1",padding:"6 12",marginRight:8,borderWidth:1,borderColor:"#475569",borderRadius:6}}>取消</div>
      <div onPointerDown={onOk} style={{fontSize:11,color:"#0f172a",backgroundColor:"#93c5fd",padding:"6 12",borderRadius:6}}>确定</div>
    </div>
  </div>
}

const HISTORY_FILE = "window-states/aichat-history.json"
const MAX_MSGS = 200

type Msg = { id: number; role: "user" | "ai"; text: string }

const splitSentences = (text: string): string[] => {
  const src = String(text || "").replace(/\r/g, "")
  const out: string[] = []
  let buf = ""
  for (let i = 0; i < src.length; i++) {
    const ch = src.charAt(i)
    if (ch === "\n") {
      const t = buf.trim()
      if (t) out.push(t)
      buf = ""
      continue
    }
    buf += ch
    if ("。！？!?…".indexOf(ch) >= 0) {
      const t = buf.trim()
      if (t) out.push(t)
      buf = ""
    }
  }
  const tail = buf.trim()
  if (tail) out.push(tail)
  return out.length > 0 ? out : (src.trim() ? [src.trim()] : [])
}

const loadHistory = (): Msg[] => {
  try {
    if (!chill.io.exists(HISTORY_FILE)) return []
    const arr = JSON.parse(chill.io.readText(HISTORY_FILE) || "[]")
    if (!Array.isArray(arr)) return []
    return arr
      .filter((m: any) => m && typeof m.text === "string" && m.text.length > 0)
      .map((m: any, i: number) => ({
        id: Number(m.id) || Date.now() + i,
        role: m.role === "user" ? "user" : "ai",
        text: String(m.text),
      }))
  } catch (_) {
    return []
  }
}

const saveHistory = (msgs: Msg[]) => {
  try {
    chill.io.writeText(HISTORY_FILE, JSON.stringify(msgs.slice(-MAX_MSGS)))
  } catch (_) {}
}

const Avatar = ({ role, size }: { role: "user" | "ai"; size: number }) => (
  <div
    style={{
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
    }}
  >
    {role === "user" ? "我" : "猫"}
  </div>
)

const Bubble = ({ m, maxW, avatar }: { m: Msg; maxW: number; avatar: number }) => (
  <div
    style={{
      display: "Flex",
      flexDirection: "Row",
      justifyContent: m.role === "user" ? "FlexEnd" : "FlexStart",
      marginBottom: 6,
      flexShrink: 0,
    }}
  >
    {m.role === "ai" ? <Avatar role="ai" size={avatar} /> : null}
    <div
      style={{
        maxWidth: maxW,
        backgroundColor: m.role === "user" ? "#3b82f6e6" : "#1f2937e6",
        color: "#f1f5f9",
        fontSize: 12,
        paddingLeft: 8,
        paddingRight: 8,
        paddingTop: 6,
        paddingBottom: 6,
        borderRadius: 10,
        whiteSpace: "Normal",
      }}
    >
      {m.text}
    </div>
    {m.role === "user" ? <Avatar role="user" size={avatar} /> : null}
  </div>
)
const ChatPanel = ({compact=false}:{compact?:boolean}) => {
  const [prompt,setPrompt] = useState("")
  const fieldRef = useRef<any>(null)
  const [rec,setRec] = useState(false)
  const [cfgMode,setCfgMode] = useState(false)
  const [draft,setDraft] = useState<KV>({})
  const [defaults,setDefaults] = useState<KV>({})
  const [status,setStatus] = useState<any>({available:false})
  const [msgs,setMsgs] = useState<Msg[]>(() => loadHistory())
  const [panelW,setPanelW] = useState(420)
  const [viewH,setViewH] = useState(0)
  const [contentH,setContentH] = useState(0)
  const [scrollY,setScrollY] = useState(0)
  const [dragging,setDragging] = useState(false)
  const [vu,setVu] = useState(0)

  const followRef = useRef(true)
  const dragRef = useRef({ active:false, startY:0, startScroll:0 })

  const maxScroll = Math.max(0, contentH - viewH)
  const clampY = (v:number) => Math.max(0, Math.min(maxScroll, v))

  useEffect(()=>{
    if (!rec) { setVu(0); return }
    const id = setInterval(()=>{
      const t = Date.now() / 160
      const noise = Math.random() * 0.5
      const level = Math.max(0.08, Math.min(1, Math.abs(Math.sin(t)) * 0.7 + noise * 0.3))
      setVu(level)
    }, 80)
    return ()=>clearInterval(id as any)
  },[rec])

  const vuStyle = useMemo(()=>({
    width: compact ? 14 : 18,
    height: compact ? 14 : 18,
    borderRadius: 999,
    backgroundColor: rec ? "#f87171" : "#64748b",
    scale: rec ? (0.9 + vu * 0.9) : 1,
    opacity: rec ? (0.5 + vu * 0.5) : 0.35,
    marginRight: 6,
    borderWidth: 1,
    borderColor: rec ? "#fecaca" : "#94a3b8",
  }),[rec,vu,compact])

  const appendMsgs = (items: Msg[]) => {
    setMsgs(prev => {
      const next = [...prev, ...items].slice(-MAX_MSGS)
      saveHistory(next)
      return next
    })
    followRef.current = true
  }

  useEffect(()=>{
    setStatus(JSON.parse(chill.aichat.getStatus()))
    setDraft(loadCfg())
    setDefaults(loadDefaults())
    const t = chill.aichat.onConversationCompleted((json:string)=>{
      try {
        const d = JSON.parse(json || "{}")
        const zh = String(d.SubtitleText || "").trim()
        if (!zh) return
        const parts = splitSentences(zh)
        if (parts.length === 0) return
        const now = Date.now()
        appendMsgs(parts.map((p, i) => ({ id: now + i, role: "ai" as const, text: p })))
      } catch (_) {}
    })
    return ()=>{ if (t) chill.aichat.offConversationCompleted(t) }
  },[])

  // 新内容进来时，若本来就在底部，就继续贴底
  useEffect(()=>{
    if (followRef.current) setScrollY(clampY(contentH - viewH))
  },[contentH, viewH, msgs.length])

  const send = ()=>{
    const f = fieldRef.current
    const ve = f?.ve ?? f
    const text = String(ve?.value ?? prompt ?? "")
    if (!text.trim()) return
    const r = call("startTextConversation",text,"wm-plugin")
    if (r.ok) {
      appendMsgs([{ id: Date.now(), role: "user", text: text.trim() }])
      if (ve) {
        if (ve.SetValueWithoutNotify) ve.SetValueWithoutNotify("")
        else ve.value = ""
      }
      setPrompt("")
      if (f) setTimeout(()=>{
        if (fieldRef.current !== f) return
        try{ if (ve?.Focus) ve.Focus(); else if (f.focus) f.focus() }catch(_){}
      }, 0)
    }
  }
  const micDown = ()=>{ const r = call("startVoiceCapture"); if (r.ok) setRec(true) }
  const micUp = ()=>{ const r = call("stopVoiceCaptureAndSend","wm-plugin"); setRec(false); if (!r.ok) console.log(r.error) }
  const save = ()=>{
    let ok = true
    for (const k of Object.keys(draft)) { const r = call("setConfig",k,String(draft[k] ?? "")); if (!r.ok) ok = false }
    const rs = call("saveConfig"); setCfgMode(false); setStatus(JSON.parse(chill.aichat.getStatus())); if (!ok || !rs.ok) console.log("保存失败")
  }

  const onScrubDown = (e:any) => {
    followRef.current = false
    dragRef.current = { active:true, startY: e?.position?.y ?? 0, startScroll: scrollY }
    setDragging(true)
  }
  const onScrubMove = (e:any) => {
    const d = dragRef.current
    if (!d.active) return
    const dy = (e?.position?.y ?? 0) - d.startY
    setScrollY(clampY(d.startScroll - dy))
  }
  const onScrubUp = () => {
    dragRef.current.active = false
    setDragging(false)
  }
  const jumpToLatest = () => {
    followRef.current = true
    setScrollY(maxScroll)
  }

  const availW = Math.max(140, panelW - 26)
  const sendW = Math.min(64, Math.max(46, Math.round(availW * 0.15)))
  const textW = Math.max(80, availW - sendW - 8)
  const lineCount = useMemo(()=>{
    const src = prompt.length > 0 ? prompt : " "
    let n = 0
    for (const part of src.split("\n")) n += Math.max(1, Math.ceil((part.length * 12) / Math.max(40, textW)))
    return Math.max(1, n)
  },[prompt, textW])
  const inputH = Math.max(32, Math.min(compact ? 64 : 116, 12 + lineCount * 17))

  const thumbH = maxScroll > 0 ? Math.max(22, Math.round(viewH * Math.min(1, viewH / Math.max(contentH, 1)))) : 0
  const thumbTop = maxScroll > 0 ? Math.round((clampY(scrollY) / maxScroll) * Math.max(0, viewH - thumbH)) : 0

  if (cfgMode && !compact) return <SavePanel draft={draft} defaults={defaults} setDraft={setDraft} onCancel={()=>setCfgMode(false)} onOk={save} />

  return <div
    onGeometryChanged={(e:any)=>{ const hh = e?.newRect?.width; if (hh) setPanelW(Math.max(220, Math.floor(hh))) }}
    style={{flexGrow:1,display:"Flex",flexDirection:"Column",backgroundColor:"transparent",padding:10,overflow:"Hidden"}}>

    {!compact && <>
      <div style={{fontSize:11,color:status.available?"#86efac":"#fca5a5",marginBottom:2,flexShrink:0}}>{status.available?"AIChat 已连接":"AIChat 未安装"}</div>
      <div style={{fontSize:10,color:"#94a3b8",marginBottom:6,flexShrink:0}}>busy:{String(!!status.isBusy)} ready:{String(!!status.isReady)} ver:{status.apiVersion || "-"}</div>
    </>}

    {!compact && <div
      onGeometryChanged={(e:any)=>{ const hh = e?.newRect?.height; if (hh) setViewH(Math.max(1, Math.floor(hh))) }}
      style={{flexGrow:1,flexShrink:1,minHeight:60,position:"Relative",overflow:"Hidden",marginBottom:6}}>

      <div
        onGeometryChanged={(e:any)=>{ const hh = e?.newRect?.height; if (hh) setContentH(Math.max(1, Math.floor(hh))) }}
        style={{position:"Absolute",left:0,right:0,top:0,display:"Flex",flexDirection:"Column",translate:`0 ${-Math.round(clampY(scrollY))}px`}}>
        {msgs.length === 0 ? <div style={{fontSize:11,color:"#94a3b8"}}>（还没有对话）</div> : null}
        {msgs.map(m => <Bubble key={m.id} m={m} maxW={Math.max(120, Math.round(panelW * 0.6))} avatar={26} />)}
      </div>

      <div onPointerDown={onScrubDown} style={{position:"Absolute",left:0,top:0,right:0,bottom:0}} />

      {maxScroll > 0 ? <div style={{position:"Absolute",right:0,top:0,width:8,height:"100%",backgroundColor:"#ffffff12",borderRadius:4}}>
        <div onPointerDown={onScrubDown}
          style={{position:"Absolute",left:1,right:1,top:thumbTop,height:thumbH,backgroundColor:"#94a3b8cc",borderRadius:3}} />
      </div> : null}

      {scrollY < maxScroll - 2 ? <div onPointerDown={jumpToLatest}
        style={{position:"Absolute",right:14,bottom:6,width:22,height:22,borderRadius:11,backgroundColor:"#1e293be6",borderWidth:1,borderColor:"#475569",display:"Flex",alignItems:"Center",justifyContent:"Center",fontSize:11,color:"#cbd5e1"}}>↓</div> : null}

      {dragging ? <div onPointerMove={onScrubMove} onPointerUp={onScrubUp} style={{position:"Absolute",left:0,top:0,right:0,bottom:0}} /> : null}
    </div>}

    <div style={{display:"Flex",flexDirection:"Row",alignItems:"FlexEnd",flexShrink:0}}>
      <textfield value={prompt} ref={(el:any)=>{ fieldRef.current = el }} onValueChanged={(e:any)=>setPrompt(e.newValue ?? "")} multiline={true}
        vertical-scroller-visibility={1}
        onKeyDown={(e:any)=>{
          const k = e.keyCode ?? e.key
          if (k === 13 || k === 10 || k === "Enter" || k === "NumpadEnter") {
            if (e.shiftKey) return
            if (e.PreventDefault) e.PreventDefault()
            if (e.StopPropagation) e.StopPropagation()
            send()
          }
        }}
        style={{width:textW,height:inputH,fontSize:12,backgroundColor:"#1f2937a6",borderWidth:1,borderColor:"#334155",color:"#e2e8f0",paddingLeft:8,paddingRight:8,paddingTop:6,paddingBottom:6}} />

      <div onPointerDown={send}
        style={{width:sendW,height:inputH,marginLeft:6,backgroundColor:"#3b82f6",borderRadius:6,display:"Flex",alignItems:"Center",justifyContent:"Center",fontSize:12,color:"#ffffff"}}>发送</div>
    </div>

    <div style={{display:"Flex",flexDirection:"Row",marginTop:6,flexShrink:0}}>
      <div onPointerDown={micDown} onPointerUp={micUp} onPointerLeave={micUp}
        style={{fontSize:11,color:rec?"#fff":"#e2e8f0",backgroundColor:rec?"#ef4444":"#334155",paddingLeft:8,paddingRight:8,paddingTop:5,paddingBottom:5,borderRadius:6,marginRight:6,display:"Flex",flexDirection:"Row",alignItems:"Center"}}><div style={vuStyle} />{rec?"松开发送":"按住说话"}</div>
      {!compact && <div onPointerDown={()=>setCfgMode(true)} style={{fontSize:11,color:"#e2e8f0",backgroundColor:"#334155",paddingLeft:8,paddingRight:8,paddingTop:5,paddingBottom:5,borderRadius:6}}>配置</div>}
    </div>
  </div>
}

__registerPlugin({
  id: "aichat",
  title: "AIChat",
  width: 520,
  height: 460,
  initialX: 120,
  initialY: 80,
  resizable: true,
  compact: { width: 420, height: 120, component: () => <ChatPanel compact={true} /> },
  launcher: {
    text: "",
    background: "#008055",
  },
  component: () => <ChatPanel />,
})
