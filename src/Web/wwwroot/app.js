'use strict';

const state = {
  jobs: [],
  job: null,
  frames: [],
  composites: [],
  composite: null,
  selectedTile: null,
  selectedForceFrameId: null,
  version: null,
  // re-merge buffer: when a 409 happens, remember the pending edit to replay on fresh revision.
  pendingEdit: null,
};

const $ = (sel) => document.querySelector(sel);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmtTime = (iso) => iso ? new Date(iso).toLocaleString() : '';

async function api(method, path, body) {
  const opts = { method, headers: {} };
  if (body instanceof FormData) {
    opts.body = body;
  } else if (body !== undefined) {
    opts.headers['Content-Type'] = 'application/json';
    opts.body = JSON.stringify(body);
  }
  const res = await fetch('/api' + path, opts);
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) throw Object.assign(new Error(data?.message || res.statusText), { status: res.status, data });
  return data;
}

function toast(msg, isError = false) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = 'toast' + (isError ? ' error' : '');
  t.hidden = false;
  clearTimeout(toast._timer);
  toast._timer = setTimeout(() => { t.hidden = true; }, 4200);
}

async function loadHealth() {
  try {
    const h = await api('GET', '/health');
    $('#health').textContent =
      `服务正常 · 数据目录 ${h.dataDir}` + (h.recoveredInterrupted ? ` · 启动时恢复了 ${h.recoveredInterrupted} 个中断候选` : '');
  } catch {
    $('#health').textContent = '服务不可用';
  }
}

// ---------- jobs ----------
async function loadJobs(selectId) {
  state.jobs = await api('GET', '/jobs');
  const ul = $('#jobList');
  ul.innerHTML = '';
  for (const job of state.jobs) {
    const li = document.createElement('li');
    li.textContent = `${job.name} (${job.frameIds.length} 帧)`;
    li.dataset.id = job.jobId;
    if (selectId === job.jobId) li.classList.add('active');
    li.onclick = () => selectJob(job.jobId);
    ul.appendChild(li);
  }
}

async function selectJob(jobId) {
  state.job = await api('GET', `/jobs/${jobId}`);
  state.frames = await api('GET', `/jobs/${jobId}/frames`);
  state.composite = null;
  state.version = null;
  $('#emptyHint').hidden = true;
  $('#framesSection').hidden = false;
  $('#compositeSection').hidden = false;
  $('#compositeDetail').hidden = true;
  $('#versionModal').hidden = true;
  document.querySelectorAll('#jobList li').forEach((li) =>
    li.classList.toggle('active', li.dataset.id === jobId));
  renderFrames();
  await loadComposites();
}

function renderFrames() {
  const ul = $('#frameList');
  ul.innerHTML = '';
  for (const f of state.frames) {
    const li = document.createElement('li');
    const trusted = f.stageCoordinatesTrusted;
    li.innerHTML = `
      <div><b>${esc(f.originalFileName)}</b>
        <span class="pill">${f.role === 'Reshoot' ? '补拍' : '常规'}</span>
        <span class="pill">${f.width}×${f.height}</span>
        ${f.magnification ? `<span class="pill">${f.magnification}x</span>` : '<span class="pill">倍率缺失</span>'}
        ${f.pixelSizeUm ? `<span class="pill">${f.pixelSizeUm}μm/px</span>` : '<span class="pill">像素尺寸缺失</span>'}
        ${trusted ? `<span class="pill">坐标可信</span>` : '<span class="pill" style="color:#ff9b9b">坐标单位缺失</span>'}
      </div>
      <div class="meta mono">${f.sha256.slice(0, 16)}… · Z=${f.zUm ?? '—'} · ${fmtTime(f.capturedAt)}</div>
      ${f.dustNote ? `<div class="meta" style="color:#ffb4b4">尘点：${esc(f.dustNote)}</div>` : ''}
      <img src="/api/frames/${f.frameId}/preview.png?t=${f.receivedAt}" alt="">`;
    ul.appendChild(li);
  }
}

$('#btnNewJob').onclick = async () => {
  const job = await api('POST', '/jobs', { name: $('#newJobName').value });
  $('#newJobName').value = '';
  toast(`已创建任务 ${job.jobId}`);
  await loadJobs(job.jobId);
  await selectJob(job.jobId);
};

$('#uploadForm').onsubmit = async (ev) => {
  ev.preventDefault();
  if (!state.job) return;
  const fd = new FormData(ev.target);
  try {
    const r = await api('POST', `/jobs/${state.job.jobId}/frames`, fd);
    toast(r.deduplicated ? `相同证据已存在，复用帧 ${r.frame.frameId}（内容寻址去重）` :
      `已接收帧 ${r.frame.frameId}，原始字节不可变存档`);
    ev.target.reset();
    await selectJob(state.job.jobId);
  } catch (e) {
    toast(e.message, true);
  }
};

// ---------- composites ----------
async function loadComposites(selectId) {
  state.composites = await api('GET', `/jobs/${state.job.jobId}/composites`);
  const ul = $('#compositeList');
  ul.innerHTML = '';
  for (const c of state.composites) {
    const li = document.createElement('li');
    li.innerHTML = `${esc(c.name)} <span class="pill">${c.state}</span>
      <span class="pill">${c.tiles.length} 瓦片</span>
      <span class="pill">rev ${c.revision}</span>
      ${c.publishedVersionId ? '<span class="pill" style="color:#9fe0b4">已发布</span>' : ''}`;
    li.dataset.id = c.compositeId;
    if ((selectId || state.composite?.compositeId) === c.compositeId) li.classList.add('active');
    li.onclick = () => selectComposite(c.compositeId);
    ul.appendChild(li);
  }
}

$('#btnNewComposite').onclick = async () => {
  const c = await api('POST', `/jobs/${state.job.jobId}/composites`, { name: $('#newCompositeName').value });
  $('#newCompositeName').value = '';
  await loadComposites(c.compositeId);
  await selectComposite(c.compositeId);
};

async function selectComposite(id) {
  state.composite = await api('GET', `/composites/${id}`);
  state.selectedTile = null;
  $('#tileDetail').hidden = true;
  $('#compositeDetail').hidden = false;
  document.querySelectorAll('#compositeList li').forEach((li) =>
    li.classList.toggle('active', li.dataset.id === id));
  renderComposite();
  await renderVersions();
}

function frameMap() {
  const m = new Map();
  for (const f of state.frames) m.set(f.frameId, f);
  return m;
}

function renderComposite() {
  const c = state.composite;
  $('#cmpState').textContent = c.state;
  $('#cmpRev').textContent = c.revision;
  const failed = c.tiles.filter((t) => t.state === 'Failed');
  const validated = c.tiles.filter((t) => t.state === 'Validated');
  $('#cmpState').textContent =
    `${c.state}（${validated.length}/${c.tiles.length} 瓦片已验证${failed.length ? `，${failed.length} 失败` : ''}）`;
  $('#cmpInterruption').hidden = !c.interruptionReason;
  $('#cmpInterruption').textContent = c.interruptionReason || '';

  // diagnostics
  const dl = $('#diagList');
  dl.innerHTML = '';
  for (const d of c.diagnostics) {
    const li = document.createElement('li');
    li.className = d.severity;
    li.textContent = `[${d.code}] ${d.message}`;
    dl.appendChild(li);
  }

  // frame exclusion checklist
  const fl = $('#frameCheckList');
  fl.innerHTML = '';
  const excludedFromGeom = new Set(c.excludedFromGeometryFrameIds);
  for (const fid of c.frameIds) {
    const f = frameMap().get(fid);
    if (!f) continue;
    const excluded = c.excludedFrameIds.includes(fid);
    const geomExcluded = excludedFromGeom.has(fid);
    const li = document.createElement('li');
    li.innerHTML = `
      <input type="checkbox" ${excluded ? 'checked' : ''}>
      <div>
        <b>${esc(f.originalFileName)}</b>
        ${geomExcluded ? '<span class="pill" style="color:#ff9b9b">对齐已排除</span>' : ''}
        <span class="meta mono">${f.sha256.slice(0, 14)}…</span>
      </div>`;
    li.querySelector('input').onchange = async (ev) => {
      await mutateWithConflict(() =>
        api('POST', `/composites/${c.compositeId}/frames/${fid}/exclusion`,
          { revision: c.revision, excluded: ev.target.checked }),
        (fresh) => mutateFrameExclusionOnLocal(fresh, fid, ev.target.checked));
    };
    fl.appendChild(li);
  }

  renderTileGrid();
}

// Local optimistic merge helper used after a conflict is resolved by re-merge.
function mutateFrameExclusionOnLocal(fresh, fid, excluded) {
  fresh.excludedFrameIds = fresh.excludedFrameIds || [];
  if (excluded) fresh.excludedFrameIds.push(fid);
  else fresh.excludedFrameIds = fresh.excludedFrameIds.filter((x) => x !== fid);
  return fresh;
}

// ---------- conflict handling ----------
async function mutateWithConflict(request, localReplay) {
  try {
    const result = await request();
    state.composite = result;
    renderComposite();
    await loadComposites();
    return result;
  } catch (e) {
    if (e.status !== 409) { toast(e.message, true); throw e; }
    const fresh = await api('GET', `/composites/${state.composite.compositeId}`);
    state.pendingEdit = { localReplay, lastError: e };
    $('#conflictMsg').textContent = e.data?.message || e.message;
    $('#conflictRev').textContent = e.data?.currentRevision ?? fresh.revision;
    $('#conflictModal').hidden = false;
    state.composite = fresh;
    renderComposite();
    await loadComposites();
    return fresh;
  }
}

$('#btnConflictReload').onclick = () => {
  state.pendingEdit = null;
  $('#conflictModal').hidden = true;
  selectComposite(state.composite.compositeId);
  toast('已加载服务器最新状态，本地未提交的改选已丢弃');
};

$('#btnConflictMerge').onclick = async () => {
  // Re-merge: open the affected tile on the fresh revision so the operator re-applies
  // their choice deliberately. The selected tile + forced frame are remembered.
  const pending = state.pendingEdit;
  $('#conflictModal').hidden = true;
  if (!pending) return;
  state.composite = await api('GET', `/composites/${state.composite.compositeId}`);
  renderComposite();
  if (state.selectedTile !== null) await selectTile(state.selectedTile);
  toast('已加载最新状态；请在右侧瓦片面板重新确认你的来源改选后再提交。');
};

// ---------- tile grid & detail ----------
function dominantTrust(counts) {
  if (!counts) return null;
  let best = null, bestN = -1;
  for (const [k, v] of Object.entries(counts)) {
    if (v > bestN) { best = k; bestN = v; }
  }
  return best;
}

function renderTileGrid() {
  const c = state.composite;
  const grid = $('#tileGrid');
  grid.innerHTML = '';
  for (const t of c.tiles) {
    const div = document.createElement('div');
    div.className = `tilecell ${t.state}` + (state.selectedTile === t.index ? ' selected' : '');
    div.title = `瓦片 ${t.index} · ${t.state}${t.failureReason ? ' · ' + t.failureReason : ''}`;
    div.innerHTML = `<span class="idx">${t.index}</span>${t.manualFrameId ? '<span class="manual">✋</span>' : ''}`;
    div.onclick = () => selectTile(t.index);
    grid.appendChild(div);
  }
  if (c.tiles.length === 0) {
    grid.innerHTML = '<div style="color:var(--muted);grid-column:1/-1">尚未生成候选。点击“生成/重试候选”。</div>';
  }
}

async function selectTile(index) {
  const c = state.composite;
  state.selectedTile = index;
  const tile = c.tiles[index];
  if (!tile) return;
  $('#tileDetail').hidden = false;
  $('#tileIndexLabel').textContent = index;
  const bust = `?rev=${c.revision}`;
  const base = `/composites/${c.compositeId}/tiles/${index}/preview.png${bust}`;
  $('#tileImg').src = base;
  $('#tileTrustImg').src = base + '&overlay=trust';
  $('#tileSourceImg').src = base + '&overlay=source';

  const table = $('#tileScoreTable');
  const rows = tile.scores.map((s) => {
    const f = frameMap().get(s.frameId);
    const chosen = s.chosenPixels;
    const isManual = tile.manualFrameId === s.frameId;
    return `<tr data-frame="${s.frameId}" class="${isManual ? 'selected-source' : ''}">
      <td><input type="radio" name="tileFrame" ${isManual ? 'checked' : ''} ${s.coveredPixels === 0 || s.excluded ? 'disabled' : ''}></td>
      <td>${esc(f?.originalFileName || s.frameId)}</td>
      <td>${s.meanSharpness.toFixed(1)}</td>
      <td>${s.coveredPixels}</td>
      <td>${chosen}</td>
      <td>${s.excluded ? '已排除' : isManual ? '手工来源' : ''}</td>
    </tr>`;
  }).join('');
  table.innerHTML = `<tr><th>选择</th><th>帧</th><th>平均清晰度分</th><th>覆盖像素</th><th>被选用像素</th><th>状态</th></tr>${rows}`;

  table.querySelectorAll('tr[data-frame]').forEach((tr) => {
    tr.onclick = () => {
      const radio = tr.querySelector('input[type=radio]');
      if (!radio.disabled) {
        radio.checked = true;
        state.selectedForceFrameId = tr.dataset.frame;
      }
    };
  });
  renderTileGrid();
}

$('#btnTileForce').onclick = async () => {
  const c = state.composite;
  if (state.selectedTile === null) return;
  const checked = document.querySelector('input[name=tileFrame]:checked');
  const tr = checked?.closest('tr');
  const frameId = tr?.dataset.frame || state.selectedForceFrameId;
  if (!frameId) { toast('请先选择一个来源帧'); return; }
  try {
    const fresh = await mutateWithConflict(() =>
      api('POST', `/composites/${c.compositeId}/tiles/${state.selectedTile}/source`,
        { revision: c.revision, frameId }));
    const done = await api('POST', `/composites/${fresh.compositeId}/reprocess`, {});
    state.composite = done;
    renderComposite();
    await selectTile(state.selectedTile);
    await loadComposites();
    toast(`瓦片 ${state.selectedTile} 已改选并重新验证`);
  } catch (e) {
    if (e.status !== 409) toast(e.message, true);
  }
};

$('#btnTileAuto').onclick = async () => {
  const c = state.composite;
  if (state.selectedTile === null) return;
  try {
    const fresh = await mutateWithConflict(() =>
      api('POST', `/composites/${c.compositeId}/tiles/${state.selectedTile}/source`,
        { revision: c.revision, frameId: null }));
    const done = await api('POST', `/composites/${fresh.compositeId}/reprocess`, {});
    state.composite = done;
    renderComposite();
    await selectTile(state.selectedTile);
    await loadComposites();
  } catch (e) {
    if (e.status !== 409) toast(e.message, true);
  }
};

$('#btnProcess').onclick = async () => {
  try {
    state.composite = await api('POST', `/composites/${state.composite.compositeId}/process`, {});
    renderComposite();
    await loadComposites();
    if (state.selectedTile !== null) await selectTile(state.selectedTile);
    toast(state.composite.state === 'Ready' ? '候选已生成：请核对瓦片与诊断后发布' : '候选处理结束，请查看诊断');
  } catch (e) { toast(e.message, true); }
};
$('#btnReprocess').onclick = async () => {
  state.composite = await api('POST', `/composites/${state.composite.compositeId}/reprocess`, {});
  renderComposite(); await loadComposites();
  toast('脏瓦片已重新验证');
};
$('#btnCancel').onclick = async () => {
  if (!confirm('取消当前候选？已发布版本不会被替换。')) return;
  state.composite = await api('POST', `/composites/${state.composite.compositeId}/cancel`,
    { revision: state.composite.revision });
  renderComposite(); await loadComposites();
  toast('候选已取消，上一已发布版本保持不变');
};
$('#btnPublish').onclick = async () => {
  try {
    const r = await api('POST', `/composites/${state.composite.compositeId}/publish`,
      { revision: state.composite.revision, label: $('#publishLabel').value });
    state.composite = r.composite;
    $('#publishLabel').value = '';
    renderComposite(); await loadComposites(); await renderVersions();
    toast(`已发布不可变版本 ${r.version.versionId}`);
  } catch (e) { toast(e.message, true); }
};

// ---------- versions ----------
async function renderVersions() {
  const c = state.composite;
  const versions = await api('GET', `/composites/${c.compositeId}/versions`);
  const ul = $('#versionList');
  ul.innerHTML = '';
  for (const v of versions) {
    const li = document.createElement('li');
    const current = c.publishedVersionId === v.versionId;
    li.innerHTML = `<b>${esc(v.label)}</b> <span class="pill">#${v.sequence}</span>
      <span class="pill">${v.ruleVersion}</span>
      <span class="pill">${v.fingerprints.length} 个输入指纹</span>
      ${current ? '<span class="pill" style="color:#9fe0b4">当前发布</span>' : ''}
      <span class="meta">${fmtTime(v.publishedAt)}</span>
      <span class="mono" style="margin-left:auto">${v.versionId}</span>`;
    li.style.cursor = 'pointer';
    li.onclick = () => openVersion(v.versionId);
    ul.appendChild(li);
  }
  if (versions.length === 0) ul.innerHTML = '<li class="meta">还没有发布版本。</li>';
}

async function openVersion(versionId) {
  const v = await api('GET', `/versions/${versionId}`);
  state.version = v;
  $('#versionModal').hidden = false;
  $('#verTitle').textContent = `${v.label} (${v.versionId})`;
  const base = `/versions/${versionId}/preview.png`;
  $('#verImg').src = base;
  $('#verTrustImg').src = base + '?overlay=trust';
  $('#verSourceImg').src = base + '?overlay=source';
  $('#verParams').textContent =
    `rule: ${v.ruleVersion}\nparent: ${v.parentVersionId || '(root)'}\n` +
    `canvas: ${v.canvasWidth}x${v.canvasHeight} @ ${v.canvasPixelSizeUm} μm/px, origin=(${v.originXUm}, ${v.originYUm})\n` +
    `excluded: ${(v.excludedFrameIds || []).join(', ') || '(none)'}\n` +
    Object.entries(v.parameters).map(([k, val]) => `${k}: ${val}`).join('\n');
  const fps = v.fingerprints.map((f) => `<tr>
      <td>${esc(f.originalFileName)}</td><td class="mono">${f.sha256.slice(0, 20)}…</td>
      <td>${f.width}×${f.height}</td><td>${f.magnification ?? '—'}x</td><td>${f.zUm ?? '—'}</td>
      <td>${fmtTime(f.capturedAt)}</td></tr>`).join('');
  $('#verFingerprints').innerHTML =
    '<tr><th>文件名</th><th>SHA-256</th><th>尺寸</th><th>倍率</th><th>Z</th><th>采集时间</th></tr>' + fps;
  $('#verExportLink').href = `/versions/${versionId}/export`;
  await renderImports();
}
$('#btnCloseVer').onclick = () => { $('#versionModal').hidden = true; };

$('#btnVerRepublish').onclick = async () => {
  try {
    const c = state.composite;
    const r = await api('POST', `/composites/${c.compositeId}/republish`,
      { revision: c.revision, versionId: state.version.versionId, label: '' });
    state.composite = r.composite;
    renderComposite(); await loadComposites(); await renderVersions();
    $('#versionModal').hidden = true;
    toast(`已按旧版本指纹重新发布为 ${r.version.versionId}`);
  } catch (e) {
    if (e.status === 409) toast('冲突：请刷新后在新版本上重试', true);
    else toast(e.message, true);
  }
};
$('#btnVerDraft').onclick = async () => {
  try {
    const c = state.composite;
    state.composite = await api('POST', `/composites/${c.compositeId}/draft-from-version`,
      { revision: c.revision, versionId: state.version.versionId });
    renderComposite(); await loadComposites();
    $('#versionModal').hidden = true;
    toast('已载入旧版本草稿：其中的输入指纹与改选均来自该不可变版本');
  } catch (e) { toast(e.message, true); }
};

// ---------- imports ----------
async function renderImports() {
  if (!state.job) return;
  const records = await api('GET', `/jobs/${state.job.jobId}/imports`);
  const ul = $('#importList');
  ul.innerHTML = records.length ? '' : '<li class="info">还没有导入记录。</li>';
  for (const r of records) {
    const a = r.audit;
    const ok = r.status === 'Verified';
    const li = document.createElement('li');
    li.className = ok ? 'info' : 'error';
    li.textContent = ok
      ? `[${r.importId}] ${r.packageName} 验证通过：逐像素核对 ${a.pixelsChecked} 个像素，0 处来源/可信度不一致。`
      : `[${r.importId}] ${r.packageName} 验证失败：来源不一致 ${a.provenanceMismatches}，可信度不一致 ${a.trustMismatches}，` +
        `缺失指纹 ${a.missingFrameFingerprints.length}。${(a.errors || []).join(' / ')}`;
    ul.appendChild(li);
  }
}

$('#importForm').onsubmit = async (ev) => {
  ev.preventDefault();
  const fd = new FormData(ev.target);
  try {
    const r = await api('POST', `/jobs/${state.job.jobId}/imports`, fd);
    if (r.status === 'Verified') toast('导入逐像素核对通过');
    else toast('导入审计未通过，详情见版本面板', true);
    ev.target.reset();
    await renderImports();
  } catch (e) { toast(e.message, true); }
};

// ---------- boot ----------
(async function boot() {
  await loadHealth();
  await loadJobs();
  if (state.jobs.length) await selectJob(state.jobs[0].jobId);
})();
