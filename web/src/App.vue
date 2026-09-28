<script setup lang="ts">
import { ref, computed } from 'vue'
const provider = ref('OpenAIOfficial'), protocol = ref('OpenAI'), key = ref(''), endpointUrl = ref(''), model = ref(''), models = ref<string[]>([]), modelError = ref(''), loadingModels = ref(false), useProxy = ref(false), result = ref(''), loading = ref(false)
type Monitor = { id:number; name:string; kind:string; provider?:string; protocol?:string; url:string; model:string; useProxy:boolean; intervalMinutes:number; emails:string[]; enabled:boolean; nextRunAt:string; lastRunAt?:string; lastSuccess?:boolean; lastMessage:string; alertActive:boolean; isRunning:boolean; runningSince?:string; hasKey:boolean; proxyUrl?:string; proxyType?:string; proxyProtocol?:string; proxyUsername?:string; hasProxyPassword?:boolean; dbKind?:string; dbType?:string; dbHost?:string; dbPort?:number; dbName?:string; dbUser?:string }
const view = ref<'check'|'monitor'>(window.location.pathname === '/monitor' ? 'monitor' : 'check'), monitors = ref<Monitor[]>([]), monitorLoading = ref(false), monitorMessage = ref('')
let monitorTimer: ReturnType<typeof setInterval> | undefined
const monitorForm = ref({name:'', kind:'http', provider:'OpenAIOfficial', protocol:'OpenAI', url:'', model:'', key:'', useProxy:false, intervalMinutes:5, emails:'', proxyType:'http', proxyProtocol:'tcp', proxyUrl:'', proxyUsername:'', proxyPassword:'', dbType:'mysql', dbHost:'', dbPort:3306, dbName:'', dbUser:'', dbPassword:''})
const showForm = ref(false), editingId = ref<number|null>(null)
const runsTask = ref<Monitor|null>(null), runsList = ref<any[]>([]), runsLoading = ref(false)
const stats = computed(() => ({total:monitors.value.length, running:monitors.value.filter(m=>m.enabled).length, alerts:monitors.value.filter(m=>m.alertActive).length, paused:monitors.value.filter(m=>!m.enabled).length}))
const stateOf = (m:Monitor) => m.isRunning ? 'checking' : !m.enabled ? 'paused' : m.alertActive ? 'alert' : m.lastSuccess === false ? 'fail' : m.lastSuccess === true ? 'ok' : 'pending'
const stateText: Record<string,string> = {checking:'检测中', paused:'已暂停', alert:'告警中', fail:'失败', ok:'正常', pending:'待执行'}
const categories = [{ id: 'llm', name: 'LLM' }, { id: 'api', name: '普通 API' }, { id: 'http', name: 'HTTP' }], category = ref('llm')
const providers = [{ id: 'OpenAIOfficial', name: 'OpenAI', type: 'llm', protocol: 'OpenAI' }, { id: 'Claude', name: 'Claude', type: 'llm', protocol: 'Anthropic' }, { id: 'LegacyChatGPT', name: 'ChatGPT', type: 'llm', protocol: 'OpenAI' }, { id: 'LegacyAnthropic', name: 'Anthropic', type: 'llm', protocol: 'Anthropic' }, { id: 'Ark', name: '方舟', type: 'llm', protocol: 'OpenAI' }, { id: 'Mimo', name: 'Mimo', type: 'llm', protocol: 'OpenAI' }, { id: 'GLM', name: 'GLM', type: 'llm', protocol: 'OpenAI' }, { id: 'Custom', name: '自定义', type: 'llm', protocol: 'OpenAI' }, { id: 'Bocha', name: 'Bocha', type: 'api', protocol: '' }, { id: 'Tavily', name: 'Tavily', type: 'api', protocol: '' }, { id: 'Brave', name: 'Brave', type: 'api', protocol: '' }]
const visibleProviders = computed(() => providers.filter(p => p.type === (category.value === 'api' ? 'api' : 'llm')))
const dbTypes = [{ id: 'mysql', name: 'MySQL / OceanBase / Manticore', port: 3306 }, { id: 'tdengine', name: 'TDengine', port: 6041 }, { id: 'milvus', name: 'Milvus', port: 19530 }, { id: 'tugraph', name: 'TuGraph', port: 7070 }]
const proxyTypes = [{ id: 'http', name: 'HTTP 代理' }, { id: 'socks5', name: 'SOCKS5 代理' }]
const dbPort = computed(() => dbTypes.find(d => d.id === monitorForm.value.dbType)?.port ?? 3306)
const selected = computed(() => providers.find(p => p.id === provider.value)), isLlm = computed(() => selected.value?.type === 'llm'), needsCustomUrl = computed(() => provider.value === 'Custom')
async function loadModels() { if (!isLlm.value || !key.value.trim() || (needsCustomUrl.value && !endpointUrl.value.trim())) { models.value = []; model.value = ''; return } loadingModels.value = true; modelError.value = ''; try { const r = await fetch('/api/models', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ provider: provider.value, protocol: protocol.value, key: key.value, endpointUrl: needsCustomUrl.value ? endpointUrl.value : null, useProxy: useProxy.value }) }); if (!r.ok) throw new Error(await r.text()); models.value = (await r.json()).models ?? []; model.value = models.value[0] ?? ''; if (!model.value) throw new Error('模型列表为空') } catch (e) { models.value = []; model.value = ''; modelError.value = e instanceof Error ? e.message : '模型获取失败，可手动填写' } finally { loadingModels.value = false } }
function select(p: typeof providers[number]) { provider.value = p.id; if (p.protocol) protocol.value = p.protocol; loadModels() }
function selectCategory(id: string) { category.value = id; if (id === 'llm' || id === 'api') { const first = providers.find(p => id === 'llm' ? p.type === 'llm' : p.type === 'api'); if (first) select(first) } }
async function check() { if (isLlm.value && !model.value.trim()) { result.value = '请先获取并选择模型'; return } loading.value = true; result.value = ''; try { const c = category.value; const path = c === 'http' ? '/api/http-check' : '/api/public-check'; const body = c === 'http' ? { url: endpointUrl.value, useProxy: useProxy.value } : { provider: provider.value, protocol: isLlm.value ? protocol.value : null, model: isLlm.value && model.value.trim() ? model.value.trim() : null, key: key.value, endpointUrl: isLlm.value && endpointUrl.value.trim() ? endpointUrl.value.trim() : null, useProxy: useProxy.value }; const r = await fetch(path, { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(body) }); result.value = r.ok ? JSON.stringify(await r.json(), null, 2) : await r.text() } catch (e) { result.value = e instanceof Error ? e.message : '请求失败' } finally { loading.value = false } }
function fmt(v?: string) { if (!v) return '—'; const d = new Date(v); return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString() }
function intervalLabel(minutes: number) { return minutes >= 1440 ? `每 ${minutes / 1440} 天` : minutes >= 60 ? `每 ${minutes / 60} 小时` : `每 ${minutes} 分钟` }
function iconOf(m: Monitor) { return m.kind === 'http' ? '⬡' : m.kind === 'llm' ? '◆' : m.kind === 'proxy' ? '⇄' : m.kind === 'db' ? '⛁' : '⌁' }
function targetOf(m: Monitor) { return m.kind === 'http' ? m.url : m.kind === 'proxy' ? `${m.proxyType ?? 'http'} · ${m.proxyUrl ?? ''}` : m.kind === 'db' ? `${m.dbType ?? ''} · ${m.dbHost ?? ''}:${m.dbPort ?? ''}` : (m.provider ?? '') + (m.model ? ' · ' + m.model : '') }
async function api(path: string, init?: RequestInit) { const r = await fetch(path, init); if (!r.ok) throw new Error(await r.text()); return r.status === 204 ? null : r.json() }
async function loadMonitors(silent = false) { if (!silent) monitorLoading.value = true; try { monitors.value = await api('/api/monitoring') } finally { monitorLoading.value = false } }
const canLoadMonitorModels = computed(() => monitorForm.value.kind === 'llm' && (! (monitorForm.value.provider === 'Custom' && !monitorForm.value.url.trim())) && (!!monitorForm.value.key.trim() || (editingId.value !== null && monitors.value.some(m => m.id === editingId.value && m.hasKey && m.provider === monitorForm.value.provider))))
async function loadModelsForMonitor() {
  if (!canLoadMonitorModels.value) return
  loadingModels.value = true; modelError.value = ''
  try {
    const f = monitorForm.value
    const savedKey = !f.key.trim() && editingId.value !== null
    const r = await fetch(savedKey ? `/api/monitoring/${editingId.value}/models` : '/api/models', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ provider: f.provider, protocol: f.protocol, ...(!savedKey ? { key: f.key } : {}), endpointUrl: f.provider === 'Custom' ? f.url : null, useProxy: f.useProxy }) })
    if (!r.ok) throw new Error(await r.text())
    models.value = (await r.json()).models ?? []
    if (!models.value.includes(f.model)) f.model = models.value[0] ?? ''
  } catch (e) { models.value = []; modelError.value = e instanceof Error ? e.message : '模型获取失败，可手动填写' }
  finally { loadingModels.value = false }
}
async function saveMonitor() { monitorMessage.value = ''; try { const f = monitorForm.value; const payload = { name: f.name, kind: f.kind, provider: f.kind === 'llm' || f.kind === 'api' ? f.provider : null, protocol: f.kind === 'llm' ? f.protocol : null, url: f.kind === 'http' ? f.url : f.kind === 'llm' && f.provider === 'Custom' ? f.url : null, model: f.kind === 'llm' ? f.model : null, key: f.kind === 'llm' || f.kind === 'api' ? f.key : '', useProxy: f.useProxy, intervalMinutes: f.intervalMinutes, emails: f.emails.split(',').map(x => x.trim()).filter(Boolean), proxyUrl: f.kind === 'proxy' ? f.proxyUrl : null, proxyProtocol: f.kind === 'proxy' ? f.proxyProtocol : 'tcp', proxyUsername: f.kind === 'proxy' ? f.proxyUsername : null, proxyPassword: f.kind === 'proxy' ? f.proxyPassword : null, dbKind: f.kind === 'db' ? f.dbType : null, dbHost: f.kind === 'db' ? f.dbHost : null, dbPort: f.kind === 'db' ? Number(f.dbPort) || 0 : 0, dbName: f.kind === 'db' ? f.dbName : null, dbUser: f.kind === 'db' ? f.dbUser : null, dbPassword: f.kind === 'db' ? f.dbPassword : null }; if (editingId.value !== null) await api(`/api/monitoring/${editingId.value}`, { method: 'PUT', headers: {'Content-Type':'application/json'}, body: JSON.stringify(payload) }); else await api('/api/monitoring', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(payload) }); showForm.value = false; await loadMonitors() } catch (e) { monitorMessage.value = e instanceof Error ? e.message : '保存失败' } }
function openCreate() { editingId.value = null; monitorForm.value = {name:'', kind:'http', provider:'OpenAIOfficial', protocol:'OpenAI', url:'', model:'', key:'', useProxy:false, intervalMinutes:5, emails:'', proxyType:'http', proxyProtocol:'tcp', proxyUrl:'', proxyUsername:'', proxyPassword:'', dbType:'mysql', dbHost:'', dbPort:3306, dbName:'', dbUser:'', dbPassword:''}; monitorMessage.value = ''; models.value = []; showForm.value = true }
function openEdit(m: Monitor) { const dbKind = m.dbKind ?? m.dbType ?? 'mysql'; const defaultPort = dbKind.toLowerCase() === 'tdengine' ? 6041 : dbKind.toLowerCase() === 'milvus' ? 19530 : dbKind.toLowerCase() === 'tugraph' ? 7070 : 3306; editingId.value = m.id; monitorForm.value = { name: m.name, kind: m.kind, provider: m.provider ?? 'OpenAIOfficial', protocol: m.protocol ?? 'OpenAI', url: m.url ?? '', model: m.model ?? '', key: '', useProxy: m.useProxy, intervalMinutes: m.intervalMinutes, emails: m.emails.join(', '), proxyType: (m as Monitor & { proxyType?: string }).proxyType ?? 'http', proxyProtocol: m.proxyProtocol ?? 'tcp', proxyUrl: m.proxyUrl ?? '', proxyUsername: m.proxyUsername ?? '', proxyPassword: '', dbType: dbKind.toLowerCase(), dbHost: m.dbHost ?? '', dbPort: m.dbPort && m.dbPort > 0 ? m.dbPort : defaultPort, dbName: m.dbName ?? '', dbUser: m.dbUser ?? '', dbPassword: '' }; monitorMessage.value = ''; models.value = []; showForm.value = true }
async function toggleMonitor(m: Monitor) { await api(`/api/monitoring/${m.id}/enabled`, { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ enabled: !m.enabled }) }); await loadMonitors(true) }
async function runMonitor(m: Monitor) { await api(`/api/monitoring/${m.id}/run`, { method: 'POST' }); await loadMonitors(true) }
async function deleteMonitor(m: Monitor) { if (!confirm(`删除监控任务「${m.name}」？`)) return; await api(`/api/monitoring/${m.id}`, { method: 'DELETE' }); await loadMonitors(true) }
async function openRuns(m: Monitor) { runsTask.value = m; runsLoading.value = true; try { runsList.value = await api(`/api/monitoring/${m.id}/runs`) } finally { runsLoading.value = false } }
function stopMonitorPolling() { if (monitorTimer) { clearInterval(monitorTimer); monitorTimer = undefined } }
function openCheck() { stopMonitorPolling(); view.value = 'check'; window.history.pushState({}, '', '/') }
function openMonitor(push = true) { view.value = 'monitor'; if (push && window.location.pathname !== '/monitor') window.history.pushState({}, '', '/monitor'); loadMonitors(); if (monitorTimer) clearInterval(monitorTimer); monitorTimer = setInterval(() => loadMonitors(true), 3000) }
window.addEventListener('popstate', () => { if (window.location.pathname === '/monitor') openMonitor(false); else openCheck() })
if (view.value === 'monitor') openMonitor(false)
</script>
<template>
  <div class="shell">
    <header class="topbar">
      <div class="brand"><span class="brand-mark">◆</span>API<span class="brand-accent">Monitor</span></div>
<nav class="tabs"><button :class="{active:view==='check'}" @click="openCheck">快速检测</button><button :class="{active:view==='monitor'}" @click="openMonitor()">监控中心</button></nav>
      <span class="live-badge"><i></i>SYSTEM LIVE</span>
    </header>
    <section v-if="view==='check'" class="page">
      <div class="page-head">
        <h1>API 状态检测</h1>
        <p>选择检测类型，快速验证服务是否正常响应。密钥仅用于本次请求，不会被保存。</p>
      </div>
      <div class="panel">
        <div class="panel-head">
          <h2>新建检测</h2>
          <span class="pill pill-green">安全连接</span>
        </div>
        <div class="panel-body">
          <aside class="type-rail">
            <div class="rail-title"><span class="step">01</span><div><strong>检测类型</strong><small>选择要检测的服务类别</small></div></div>
            <div class="type-tabs">
              <button v-for="c in categories" :key="c.id" :class="{active:category===c.id}" @click="selectCategory(c.id)">{{c.name}}</button>
            </div>
            <div class="rail-note"><strong>当前模式</strong><span>{{category==='llm' ? '真实对话与模型可用性' : category==='api' ? '普通 API 接口连通性' : '健康检查 URL 状态码'}}</span></div>
          </aside>
          <div class="form-area">
            <template v-if="category!=='http'">
              <div class="block">
                <div class="block-head"><span class="step">02</span><div><strong>服务配置</strong><small>选择供应商并填写访问密钥</small></div></div>
                <label class="fld-label">供应商</label>
                <div class="chips"><button v-for="p in visibleProviders" :key="p.id" :class="{active:provider===p.id}" @click="select(p)">{{p.name}}</button></div>
                <template v-if="isLlm">
                  <div class="two-col">
                    <div class="fld"><label class="fld-label">协议</label><select v-model="protocol" :disabled="!needsCustomUrl" @change="loadModels"><option value="OpenAI">OpenAI</option><option value="Anthropic">Anthropic</option></select><small v-if="!needsCustomUrl" class="fld-hint">固定为 {{protocol}}</small></div>
                    <div v-if="needsCustomUrl" class="fld"><label class="fld-label">自定义基础 URL</label><input v-model="endpointUrl" type="url" placeholder="https://your-provider.example.com/v1" maxlength="500" @change="loadModels"/><small class="fld-hint">填写到 /v1，系统自动追加协议路径</small></div>
                  </div>
                </template>
                <div class="fld"><label class="fld-label">API Key</label><input v-model="key" type="password" placeholder="粘贴你的 API Key" maxlength="512" @change="loadModels"/></div>
              </div>
              <div v-if="isLlm" class="block model-block">
                <div class="block-head"><span class="step">03</span><div><strong>模型选择</strong><small>选择用于真实对话检测的模型</small></div><span v-if="models.length" class="count">{{models.length}} 个模型</span></div>
                <div class="model-row">
                  <select v-if="models.length" v-model="model" class="model-input"><option v-for="m in models" :key="m" :value="m">{{m}}</option></select>
                  <input v-else v-model="model" class="model-input" placeholder="模型 ID（获取失败时可手动填写）" maxlength="120"/>
                  <button type="button" class="btn-ghost model-btn" :disabled="loadingModels || !key || (needsCustomUrl && !endpointUrl)" @click="loadModels"><span :class="{spinning:loadingModels}">↻</span>{{loadingModels ? ' 获取中' : ' 获取模型'}}</button>
                </div>
                <small class="model-hint" :class="{err:modelError, ok:!modelError && models.length}"><i></i>{{modelError || (models.length ? `已获取 ${models.length} 个模型` : '自动从 /models 获取，失败时可手动填写')}}</small>
              </div>
            </template>
            <div v-else-if="category==='http'" class="block">
              <div class="block-head"><span class="step">02</span><div><strong>目标地址</strong><small>检测 URL 是否返回 200</small></div></div>
              <div class="fld"><label class="fld-label">URL</label><input v-model="endpointUrl" type="url" placeholder="https://example.com/health" maxlength="500"/><small class="fld-hint">发送 GET 请求，返回 HTTP 200 判定为成功</small></div>
            </div>
            <div v-if="category!=='proxy' && category!=='db'" class="proxy-row">
              <label class="proxy-toggle"><span class="switch"><input v-model="useProxy" type="checkbox" @change="category!=='http' && isLlm && loadModels()"/><span class="slider"></span></span><span><strong>使用服务端代理</strong><small>通过服务器网络访问目标服务</small></span></label>
              <span class="proxy-state">{{useProxy ? '已开启' : '未开启'}}</span>
            </div>
            <button class="btn-primary" :disabled="loading || (category==='http' ? !endpointUrl.trim() : !key || (isLlm && !model))" @click="check">{{loading ? '正在检测…' : '开始真实检测'}} <span class="arrow">→</span></button>
            <div v-if="result" class="result-box"><div class="result-title">检测结果</div><pre>{{result}}</pre></div>
          </div>
        </div>
      </div>
    </section>

    <section v-else class="page">
      <div class="page-head row-head">
        <div><h1>监控中心</h1><p>由后端按计划执行检测，异常时通过邮箱发送告警。</p></div>
        <div class="head-actions">
          <button class="btn-ghost" @click="loadMonitors()">↻ 刷新</button>
          <button class="btn-primary btn-inline" @click="openCreate">＋ 新建监控</button>
        </div>
      </div>
      <div class="stats">
        <div class="stat"><small>全部任务</small><strong>{{stats.total}}</strong></div>
        <div class="stat stat-ok"><small>运行中</small><strong>{{stats.running}}</strong></div>
        <div class="stat stat-alert"><small>告警中</small><strong>{{stats.alerts}}</strong></div>
        <div class="stat"><small>已暂停</small><strong>{{stats.paused}}</strong></div>
      </div>
      <div v-if="monitorLoading" class="empty">正在加载监控任务…</div>
      <div v-else-if="!monitors.length" class="empty-card">
        <div class="empty-icon">◎</div>
        <strong>还没有监控任务</strong>
        <p>创建定时检测任务后，后端将自动执行并在异常时推送邮箱告警。</p>
        <button class="btn-primary btn-inline" @click="openCreate">＋ 新建监控</button>
      </div>
      <div v-else class="cards">
        <article v-for="m in monitors" :key="m.id" class="mcard" :class="stateOf(m)">
          <div class="mcard-top">
            <span class="micon" :class="stateOf(m)">{{iconOf(m)}}</span>
            <div class="mhead"><strong>{{m.name}}</strong><small>{{targetOf(m)}}</small></div>
            <span class="status" :class="stateOf(m)"><i></i>{{stateText[stateOf(m)]}}</span>
          </div>
          <div class="mmeta">
            <span>{{m.kind.toUpperCase()}}</span>
            <span>{{intervalLabel(m.intervalMinutes)}}</span>
            <span>{{m.emails.length ? m.emails.length + ' 个告警邮箱' : '未配置告警'}}</span>
          </div>
          <p class="mmsg" :class="stateOf(m)">{{m.isRunning ? '检测执行中，请稍候…' : (m.lastMessage || '等待首次检测')}}</p>
          <div class="mfoot"><small>上次 {{fmt(m.lastRunAt)}}</small><small>下次 {{fmt(m.nextRunAt)}}</small></div>
          <div class="mops">
            <button class="op" @click="openRuns(m)">记录</button>
            <button class="op" @click="openEdit(m)">编辑</button>
            <button class="op" :disabled="m.isRunning" @click="runMonitor(m)">{{m.isRunning ? '执行中…' : '立即检测'}}</button>
            <button class="op" @click="toggleMonitor(m)">{{m.enabled ? '暂停' : '启用'}}</button>
            <button class="op op-danger" @click="deleteMonitor(m)">删除</button>
          </div>
        </article>
      </div>
    </section>
  </div>

  <div v-if="showForm" class="modal-mask" @wheel.stop @touchmove.stop>
    <form class="modal wizard" @submit.prevent="saveMonitor">
      <div class="modal-head">
        <div class="modal-title">
          <strong>{{editingId ? '编辑监控任务' : '新建监控任务'}}</strong>
          <small>{{editingId ? '修改检测配置与告警设置' : '由后端按间隔自动执行，异常时推送告警'}}</small>
        </div>
        <button type="button" class="modal-close" @click="showForm=false">✕</button>
      </div>
      <div class="modal-body">
        <div class="kind-picker">
          <button type="button" v-for="k in [{id:'http',icon:'⬡',name:'HTTP 监控',desc:'检测 URL 返回 200'},{id:'llm',icon:'◆',name:'LLM 模型',desc:'真实对话可用性'},{id:'api',icon:'⌁',name:'普通 API',desc:'接口连通性'},{id:'proxy',icon:'⇄',name:'代理检测',desc:'HTTP / SOCKS5 连通性'},{id:'db',icon:'⛁',name:'数据库',desc:'MySQL / TDengine 等'}]" :key="k.id"
            class="kind-card" :class="{active:monitorForm.kind===k.id,locked:editingId!==null&&monitorForm.kind!==k.id}" :disabled="editingId!==null"
            @click="monitorForm.kind = k.id">
            <span class="kind-icon">{{k.icon}}</span>
            <span class="kind-text"><strong>{{k.name}}</strong><small>{{k.desc}}</small></span>
            <span class="kind-check">✓</span>
          </button>
        </div>

        <div class="wgroup">
          <div class="wgroup-head"><span>基本信息</span></div>
          <div class="fld"><label class="fld-label">任务名称</label><input v-model="monitorForm.name" required placeholder="例如：生产环境健康检查"/></div>
        </div>

        <div class="wgroup">
          <div class="wgroup-head"><span>检测目标</span><small>{{monitorForm.kind==='http' ? '返回 HTTP 200 判定为正常' : monitorForm.kind==='proxy' ? '通过代理访问 google.com 验证' : monitorForm.kind==='db' ? '凭据加密保存，仅用于后端检测' : '凭据加密保存，仅用于后端检测'}}</small></div>
          <div v-if="monitorForm.kind==='http'" class="fld"><label class="fld-label">目标 URL</label><input v-model="monitorForm.url" type="url" required placeholder="https://example.com/health"/></div>
          <template v-else-if="monitorForm.kind==='proxy'">
            <div class="fld"><label class="fld-label">代理类型</label>
              <div class="chips"><button type="button" v-for="p in proxyTypes" :key="p.id" :class="{active:monitorForm.proxyType===p.id}" @click="monitorForm.proxyType=p.id">{{p.name}}</button></div>
            </div>
            <div class="fld"><label class="fld-label">代理地址</label><input v-model="monitorForm.proxyUrl" required placeholder="http://127.0.0.1:7890 或 socks5://127.0.0.1:1080"/><small class="fld-hint">检测客户端到代理的传输能力；HTTP 代理支持 TCP，SOCKS5 支持 TCP 或 UDP。</small></div>
            <div class="fld"><label class="fld-label">代理协议</label><select v-model="monitorForm.proxyProtocol"><option value="tcp">TCP（CONNECT）</option><option value="udp" :disabled="monitorForm.proxyType !== 'socks5'">UDP（UDP ASSOCIATE，仅 SOCKS5）</option></select></div>
            <div class="grid-2"><div class="fld"><label class="fld-label">用户名（可选）</label><input v-model="monitorForm.proxyUsername" autocomplete="username" placeholder="代理认证用户名"/></div>
            <div class="fld"><label class="fld-label">密码（可选）</label><input v-model="monitorForm.proxyPassword" type="password" autocomplete="current-password" :placeholder="editingId ? '留空则保持原密码不变' : '代理认证密码'"/></div></div>
          </template>
          <template v-else-if="monitorForm.kind==='db'">
            <div class="fld"><label class="fld-label">数据库类型</label>
              <div class="chips"><button type="button" v-for="d in dbTypes" :key="d.id" :class="{active:monitorForm.dbType===d.id}" @click="monitorForm.dbType=d.id; monitorForm.dbPort=d.port">{{d.name}}</button></div>
            </div>
            <div class="grid-2">
              <div class="fld"><label class="fld-label">主机</label><input v-model="monitorForm.dbHost" required placeholder="127.0.0.1"/></div>
              <div class="fld"><label class="fld-label">端口</label><input v-model.number="monitorForm.dbPort" type="number" :placeholder="String(dbPort)"/></div>
            </div>
            <div class="grid-2">
              <div class="fld"><label class="fld-label">用户名</label><input v-model="monitorForm.dbUser" placeholder="root"/></div>
              <div class="fld"><label class="fld-label">密码</label><input v-model="monitorForm.dbPassword" type="password" :placeholder="editingId ? '留空则保持原密码不变' : '数据库密码'"/></div>
            </div>
            <div class="fld"><label class="fld-label">数据库名（可选）</label><input v-model="monitorForm.dbName" placeholder="留空则只验证连接"/></div>
          </template>
          <template v-else>
            <div class="fld"><label class="fld-label">供应商</label>
              <div class="chips"><button type="button" v-for="p in providers.filter(x => x.type === (monitorForm.kind === 'api' ? 'api' : 'llm'))" :key="p.id" :class="{active:monitorForm.provider===p.id}" @click="monitorForm.provider = p.id; if (p.protocol) monitorForm.protocol = p.protocol">{{p.name}}</button></div>
            </div>
            <template v-if="monitorForm.kind==='llm'">
              <div class="fld"><label class="fld-label">协议</label><select v-model="monitorForm.protocol" :disabled="monitorForm.provider!=='Custom'"><option value="OpenAI">OpenAI</option><option value="Anthropic">Anthropic</option></select></div>
              <div v-if="monitorForm.provider==='Custom'" class="fld"><label class="fld-label">自定义基础 URL</label><input v-model="monitorForm.url" type="url" required placeholder="https://your-provider.example.com/v1"/><small class="fld-hint">填写到 /v1，系统将从该地址获取模型列表</small></div>
              <div class="fld"><label class="fld-label">API Key</label><input v-model="monitorForm.key" type="password" :required="editingId===null" :placeholder="editingId ? '留空则保持原密钥不变' : '用于后端定时检测，加密保存'" maxlength="512" @change="loadModelsForMonitor"/><small class="fld-hint">先填写 Key，再获取该供应商可用的模型列表</small></div>
              <div class="fld"><label class="fld-label">模型</label>
                <div class="model-row">
                  <select v-if="models.length" v-model="monitorForm.model" class="model-input"><option v-for="m in models" :key="m" :value="m">{{m}}</option></select>
                  <input v-else v-model="monitorForm.model" class="model-input" required placeholder="模型 ID"/>
                  <button type="button" class="btn-ghost model-btn" :disabled="loadingModels || !canLoadMonitorModels" @click="loadModelsForMonitor"><span :class="{spinning:loadingModels}">↻</span>{{loadingModels ? ' 获取中' : (editingId !== null && !monitorForm.key ? ' 使用已保存 Key 获取' : ' 获取模型')}}</button>
                </div>
              </div>
            </template>
          </template>
          <div v-if="monitorForm.kind!=='proxy' && monitorForm.kind!=='db'" class="proxy-row" style="margin-bottom:0">
            <label class="proxy-toggle"><span class="switch"><input v-model="monitorForm.useProxy" type="checkbox"/><span class="slider"></span></span><span><strong>使用服务端代理</strong><small>通过服务器网络访问目标服务</small></span></label>
            <span class="proxy-state">{{monitorForm.useProxy ? '已启用代理' : '未启用代理'}}</span>
          </div>
        </div>

        <div class="wgroup">
          <div class="wgroup-head"><span>调度与告警</span><small>异常时向以下邮箱发送通知</small></div>
          <div class="grid-2">
            <div class="fld"><label class="fld-label">检测间隔</label><select v-model.number="monitorForm.intervalMinutes"><option :value="1">每 1 分钟</option><option :value="5">每 5 分钟</option><option :value="15">每 15 分钟</option><option :value="60">每小时</option><option :value="1440">每天</option></select></div>
            <div class="fld"><label class="fld-label">告警邮箱</label><input v-model="monitorForm.emails" placeholder="多个邮箱用逗号分隔"/></div>
          </div>
        </div>
        <p v-if="monitorMessage" class="msg">{{monitorMessage}}</p>
      </div>
      <div class="modal-foot">
        <span class="foot-hint">{{monitorForm.kind==='http' ? 'HTTP 任务无需密钥' : monitorForm.kind==='proxy' ? '代理任务无需密钥' : monitorForm.kind==='db' ? '数据库凭据将加密存储' : 'API Key 将加密存储'}}</span>
        <div class="foot-btns">
          <button type="button" class="btn-ghost" @click="showForm=false">取消</button>
          <button class="btn-primary btn-inline" type="submit">{{editingId?'保存修改':'创建监控任务'}}</button>
        </div>
      </div>
    </form>
  </div>

  <div v-if="runsTask" class="modal-mask" @click.self="runsTask=null" @wheel.stop @touchmove.stop>
    <div class="drawer">
      <div class="modal-head">
        <h2>{{runsTask.name}} · 执行记录</h2>
        <button type="button" class="modal-close" @click="runsTask=null">✕</button>
      </div>
      <div class="modal-body">
        <div v-if="runsLoading" class="empty">加载中…</div>
        <div v-else-if="!runsList.length" class="empty">暂无执行记录</div>
        <div v-else class="runs">
          <div v-for="r in runsList" :key="r.id" class="run-row">
            <span class="dot" :class="r.success?'ok':'bad'"></span>
            <div class="run-info"><strong>{{r.success?'成功':'失败'}}</strong><small>{{fmt(r.createdAt)}} · {{r.durationMs}}ms</small></div>
            <small class="run-msg">{{r.message}}</small>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
<style scoped>
.shell{min-height:100vh;background:#f4f6fa;color:#172033;font-family:Inter,ui-sans-serif,system-ui,sans-serif}
.topbar{position:sticky;top:0;z-index:20;height:64px;display:flex;align-items:center;gap:28px;padding:0 clamp(20px,6vw,72px);background:#ffffffe8;backdrop-filter:blur(10px);border-bottom:1px solid #e4e9f1}
.brand{display:flex;align-items:center;gap:8px;font-size:19px;font-weight:800;letter-spacing:-.5px}
.brand-mark{color:#287fca;font-size:16px}.brand-accent{color:#287fca}
.tabs{display:flex;gap:4px;background:#f0f3f8;border:1px solid #e3e8f0;border-radius:9px;padding:4px}
.tabs button{border:0;background:transparent;padding:7px 16px;border-radius:6px;font-size:13px;color:#68788c;cursor:pointer;transition:.15s}
.tabs button.active{background:#fff;color:#17669f;font-weight:700;box-shadow:0 1px 4px #233b5a14}
.live-badge{margin-left:auto;display:flex;align-items:center;gap:7px;font-size:12px;color:#277b54;background:#edf9f2;border:1px solid #bfe8cf;border-radius:999px;padding:6px 12px}
.live-badge i,.dot-live{width:7px;height:7px;border-radius:50%;background:#27b26b;box-shadow:0 0 0 4px #27b26b22}
.page{max-width:1200px;margin:0 auto;padding:36px clamp(20px,4vw,48px) 56px}
.page-head{margin-bottom:26px}
.page-head h1{font-size:28px;letter-spacing:-.8px;margin:0 0 6px}
.page-head p{margin:0;color:#68788c;font-size:14px}
.row-head{display:flex;align-items:flex-end;justify-content:space-between;gap:16px}
.head-actions{display:flex;gap:10px;align-items:center}
.panel{background:#fff;border:1px solid #e4e9f1;border-radius:14px;box-shadow:0 14px 40px #233b5a0f;overflow:hidden}
.panel-head{display:flex;align-items:center;justify-content:space-between;padding:18px 24px;border-bottom:1px solid #edf1f6}
.panel-head h2{font-size:16px;margin:0}
.pill{font-size:12px;font-weight:600;border-radius:999px;padding:5px 11px;white-space:nowrap}
.pill-green{color:#277b54;background:#edf9f2;border:1px solid #bfe8cf}
.pill-blue{color:#17669f;background:#eef6ff;border:1px solid #c9e2fb}
.pill-red{color:#b42318;background:#fdf0ef;border:1px solid #f3c6c2}
.panel-body{display:grid;grid-template-columns:240px 1fr;min-height:480px}
.type-rail{border-right:1px solid #edf1f6;background:#fafbfd;padding:22px;display:flex;flex-direction:column;gap:16px}
.rail-title,.block-head{display:flex;gap:10px;align-items:center}
.rail-title small,.block-head small{display:block;color:#7c8aa0;font-size:12px;margin-top:2px}
.step{width:26px;height:26px;border-radius:8px;background:#eaf3fd;color:#17669f;font-size:12px;font-weight:800;display:flex;align-items:center;justify-content:center;flex:none}
.type-tabs{display:flex;flex-direction:column;gap:8px}
.type-tabs button{border:1px solid #e3e8f0;background:#fff;padding:11px 14px;border-radius:9px;text-align:left;font-size:14px;color:#53677d;cursor:pointer;transition:.15s}
.type-tabs button:hover{border-color:#b8d4ee}
.type-tabs button.active{border-color:#287fca;background:#f2f8ff;color:#17669f;font-weight:600}
.rail-note{margin-top:auto;background:#fff;border:1px dashed #d4dde8;border-radius:10px;padding:12px;font-size:12px;color:#68788c;display:flex;flex-direction:column;gap:4px}
.rail-note strong{color:#172033}
.form-area{padding:26px 28px;display:flex;flex-direction:column;gap:20px}
.block{border:1px solid #edf1f6;border-radius:12px;padding:18px;background:#fff}
.block-head{margin-bottom:14px}
.block-head strong{font-size:14px}
.count{margin-left:auto;font-size:12px;color:#17669f;background:#eef6ff;border:1px solid #c9e2fb;border-radius:999px;padding:3px 10px}
.chips{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:12px}
.chips button{border:1px solid #e3e8f0;background:#fff;padding:8px 14px;border-radius:8px;font-size:13px;color:#53677d;cursor:pointer;transition:.15s}
.chips button:hover{border-color:#b8d4ee}
.chips button.active{border-color:#287fca;background:#f2f8ff;color:#17669f;font-weight:600}
.fld{display:flex;flex-direction:column;gap:6px;margin-bottom:12px}
.fld-label{font-size:12px;font-weight:700;color:#3c4b60}
.fld-hint{color:#8b98a9;font-size:12px}
input,select{width:100%;box-sizing:border-box;padding:11px 12px;border:1px solid #dbe2ec;border-radius:8px;font-size:14px;background:#fff;color:#172033;transition:.15s}
input::placeholder{color:#a5b0bf}
input:focus,select:focus{outline:0;border-color:#287fca;box-shadow:0 0 0 3px #287fca1f}
.two-col{display:grid;grid-template-columns:1fr 1fr;gap:12px}
.model-row{display:grid;grid-template-columns:1fr auto;gap:10px}
.model-btn{display:flex;align-items:center;gap:6px;white-space:nowrap}
.spinning{display:inline-block;animation:rot 1s linear infinite}
@keyframes rot{to{transform:rotate(360deg)}}
.model-hint{display:flex;align-items:center;gap:6px;margin-top:8px;font-size:12px;color:#8b98a9}
.model-hint i{width:7px;height:7px;border-radius:50%;background:#c3ccd8;flex:none}
.model-hint.ok{color:#277b54}.model-hint.ok i{background:#27b26b}
.model-hint.err{color:#b42318}.model-hint.err i{background:#e5484d}
.proxy-row{display:flex;align-items:center;justify-content:space-between;gap:12px;border:1px solid #edf1f6;border-radius:10px;padding:12px 14px;background:#fafbfd}
.proxy-toggle{display:flex;align-items:center;gap:10px;cursor:pointer}
.proxy-toggle strong{display:block;font-size:13px}
.proxy-toggle small{display:block;font-size:12px;color:#8b98a9;margin-top:1px}
.proxy-inline{margin:4px 0 12px}
.switch{position:relative;width:38px;height:22px;flex:none}
.switch input{position:absolute;opacity:0;width:100%;height:100%;margin:0;cursor:pointer}
.slider{position:absolute;inset:0;background:#d5dde7;border-radius:999px;transition:.2s}
.slider:before{content:'';position:absolute;top:3px;left:3px;width:16px;height:16px;border-radius:50%;background:#fff;box-shadow:0 1px 3px #233b5a33;transition:.2s}
.switch input:checked+.slider{background:#287fca}
.switch input:checked+.slider:before{transform:translateX(16px)}
.proxy-state{font-size:12px;color:#8b98a9}
.btn-primary{width:100%;padding:12px;border:0;border-radius:9px;background:#287fca;color:#fff;font-weight:700;font-size:14px;cursor:pointer;transition:.15s}
.btn-primary:hover:not(:disabled){background:#1f6fb8}
.btn-primary:disabled{opacity:.5;cursor:not-allowed}
.btn-inline{width:auto;padding:10px 18px}
.arrow{margin-left:4px}
.btn-ghost{border:1px solid #dbe2ec;background:#fff;padding:9px 14px;border-radius:8px;font-size:13px;color:#53677d;cursor:pointer;transition:.15s}
.btn-ghost:hover:not(:disabled){border-color:#287fca;color:#17669f}
.btn-ghost:disabled{opacity:.5;cursor:not-allowed}
.msg{font-size:13px;color:#b42318;margin:4px 0 0}
.result-box{border:1px solid #edf1f6;border-radius:10px;background:#f8fafc;padding:14px}
.result-title{font-size:12px;font-weight:700;color:#3c4b60;margin-bottom:8px}
.result-box pre{margin:0;white-space:pre-wrap;font-size:12.5px;color:#33455c}
.empty{padding:36px;text-align:center;color:#8b98a9;font-size:13px}
.stats{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin-bottom:20px}
.stat{background:#fff;border:1px solid #e4e9f1;border-radius:12px;padding:16px 18px;display:flex;flex-direction:column;gap:4px}
.stat small{color:#8b98a9;font-size:12px}
.stat strong{font-size:26px;letter-spacing:-.5px}
.stat-ok strong{color:#277b54}
.stat-alert strong{color:#b42318}
.empty-card{background:#fff;border:1px dashed #d4dde8;border-radius:14px;padding:56px 24px;text-align:center;display:flex;flex-direction:column;align-items:center;gap:8px}
.empty-icon{font-size:34px;color:#b8d4ee}
.empty-card strong{font-size:15px}
.empty-card p{margin:0 0 10px;color:#68788c;font-size:13px}
.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(340px,1fr));gap:16px}
.mcard{background:#fff;border:1px solid #e4e9f1;border-radius:12px;padding:18px;display:flex;flex-direction:column;gap:12px;transition:.15s}
.mcard:hover{box-shadow:0 10px 30px #233b5a14}
.mcard.alert{border-color:#f3c6c2}
.mcard-top{display:flex;align-items:flex-start;gap:12px}
.micon{width:40px;height:40px;border-radius:10px;display:flex;align-items:center;justify-content:center;font-size:17px;background:#eef4fb;color:#287fca;flex:none}
.micon.ok{background:#edf9f2;color:#277b54}
.micon.fail,.micon.alert{background:#fdf0ef;color:#b42318}
.micon.checking{background:#eef6ff;color:#17669f;animation:pulse 1.4s ease-in-out infinite}
.micon.paused{background:#f1f4f8;color:#8b98a9}
@keyframes pulse{50%{opacity:.5}}
.mhead{flex:1;min-width:0;display:flex;flex-direction:column;gap:2px}
.mhead strong{font-size:14px}
.mhead small{color:#8b98a9;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.status{display:flex;align-items:center;gap:6px;font-size:12px;font-weight:600;border-radius:999px;padding:4px 10px;white-space:nowrap;flex:none}
.status i{width:6px;height:6px;border-radius:50%;background:currentColor}
.status.ok{color:#277b54;background:#edf9f2}
.status.fail,.status.alert{color:#b42318;background:#fdf0ef}
.status.checking{color:#17669f;background:#eef6ff}
.status.paused,.status.pending{color:#68788c;background:#f1f4f8}
.mmeta{display:flex;gap:8px;flex-wrap:wrap}
.mmeta span{font-size:11px;color:#53677d;background:#f4f6fa;border:1px solid #e7ecf2;border-radius:6px;padding:3px 8px}
.mmsg{margin:0;font-size:12.5px;color:#53677d;overflow:hidden;text-overflow:ellipsis;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;min-height:32px}
.mmsg.fail,.mmsg.alert{color:#b42318}
.mmsg.checking{color:#17669f}
.mfoot{display:flex;justify-content:space-between;gap:8px;border-top:1px solid #f1f4f8;padding-top:10px}
.mfoot small{color:#8b98a9;font-size:11.5px}
.mops{display:flex;gap:6px;flex-wrap:wrap}
.op{border:1px solid #e3e8f0;background:#fff;padding:6px 11px;border-radius:7px;font-size:12px;color:#53677d;cursor:pointer;transition:.15s}
.op:hover:not(:disabled){border-color:#287fca;color:#17669f}
.op:disabled{opacity:.5;cursor:not-allowed}
.op-danger:hover{border-color:#e5484d;color:#b42318}
.modal-mask{position:fixed;inset:0;background:#1b283899;backdrop-filter:blur(3px);z-index:50;display:flex;align-items:center;justify-content:center;padding:20px;overflow:hidden;overscroll-behavior:contain}
.modal{background:#fff;border-radius:14px;width:min(640px,100%);max-height:88vh;display:flex;flex-direction:column;box-shadow:0 24px 60px #1b28384d}
.modal-head{display:flex;align-items:flex-start;justify-content:space-between;gap:12px;padding:18px 22px;border-bottom:1px solid #edf1f6}
.modal-title{display:flex;flex-direction:column;gap:3px}
.modal-title strong{font-size:16px;color:#172033}
.modal-title small{font-size:12px;color:#8b98a9}
.modal-close{border:0;background:#f1f4f8;width:30px;height:30px;border-radius:8px;color:#68788c;cursor:pointer;font-size:13px;flex:none}
.modal-close:hover{background:#e6ebf1;color:#172033}
.modal-body{padding:18px 22px;overflow-y:auto}
.modal-foot{display:flex;align-items:center;justify-content:space-between;gap:10px;padding:14px 22px;border-top:1px solid #edf1f6;background:#fafbfd;border-radius:0 0 14px 14px}
.foot-hint{font-size:12px;color:#8b98a9}
.foot-btns{display:flex;gap:10px}
.kind-picker{display:grid;grid-template-columns:repeat(3,1fr);gap:10px;margin-bottom:16px}
.kind-card{position:relative;display:flex;flex-direction:column;align-items:flex-start;gap:8px;border:1.5px solid #e3e8f0;background:#fff;border-radius:11px;padding:13px 14px;cursor:pointer;text-align:left;transition:.15s}
.kind-card:hover:not(:disabled){border-color:#b8d4ee}
.kind-card.active{border-color:#287fca;background:#f5faff;box-shadow:0 0 0 3px #287fca1f}
.kind-card.locked{opacity:.45;cursor:not-allowed}
.kind-icon{display:flex;align-items:center;justify-content:center;width:30px;height:30px;border-radius:8px;background:#f1f5fa;color:#287fca;font-size:15px}
.kind-card.active .kind-icon{background:#287fca;color:#fff}
.kind-text{display:flex;flex-direction:column;gap:2px}
.kind-text strong{font-size:13px;color:#2a3a52}
.kind-text small{font-size:11px;color:#8b98a9}
.kind-check{position:absolute;top:10px;right:10px;width:18px;height:18px;border-radius:50%;background:#287fca;color:#fff;font-size:11px;line-height:18px;text-align:center;opacity:0;transform:scale(.6);transition:.15s}
.kind-card.active .kind-check{opacity:1;transform:scale(1)}
.wgroup{border:1px solid #edf1f6;border-radius:11px;padding:14px 16px;margin-bottom:14px}
.wgroup-head{display:flex;align-items:baseline;justify-content:space-between;gap:10px;margin-bottom:12px}
.wgroup-head span{font-size:13px;font-weight:700;color:#2a3a52}
.wgroup-head small{font-size:11.5px;color:#8b98a9}
.wgroup .fld:last-child{margin-bottom:0}
.drawer{background:#fff;width:min(480px,100%);height:100%;max-height:100%;border-radius:0;display:flex;flex-direction:column;margin-left:auto;box-shadow:-16px 0 48px #1b283833}
.runs{display:flex;flex-direction:column;gap:8px}
.run-row{display:flex;align-items:center;gap:10px;border:1px solid #edf1f6;border-radius:10px;padding:10px 12px}
.dot{width:8px;height:8px;border-radius:50%;flex:none}
.dot.ok{background:#27b26b}.dot.bad{background:#e5484d}
.run-info{flex:none;display:flex;flex-direction:column}
.run-info strong{font-size:12.5px}
.run-info small{color:#8b98a9;font-size:11px}
.run-msg{margin-left:auto;color:#68788c;font-size:11.5px;max-width:50%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
@media (max-width:900px){
  .panel-body{grid-template-columns:1fr}
  .type-rail{border-right:0;border-bottom:1px solid #edf1f6}
  .type-tabs{flex-direction:row}
  .rail-note{margin-top:0}
  .two-col{grid-template-columns:1fr}
  .stats{grid-template-columns:repeat(2,1fr)}
  .row-head{flex-direction:column;align-items:flex-start}
  .modal-mask{padding:0;align-items:stretch}
  .modal{border-radius:0;max-height:100vh;width:100%}
}
</style>