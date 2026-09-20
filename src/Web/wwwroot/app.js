"use strict";

const state = {
  compositions: [],
  currentId: null,
  detail: null,
  versionId: null,
  selectedTile: null,
  editingBase: null,
  pendingOps: [],
  conflict: null,
  scale: 1,
};

const $ = (id) => document.getElementById(id);
async function api(method, url, body, isForm) {
  const opts = { method, headers: {} };
  if (body && !isForm) {
    opts.headers["Content-Type"] = "application/json";
    opts.body = JSON.stringify(body);
  } else if (body) {
    opts.body = body;
  }
  const res = await fetch(url, opts);
  const ct = res.headers.get("content-type") || "";
  const payload = ct.includes("json") ? await res.json() : await res.text();
  if (!res.ok) throw Object.assign(new Error(payload.error || res.statusText), { status: res.status, payload });
  return payload;
}

// ---------- tabs ----------
document.querySelectorAll(".tabs button").forEach((b) => {
  b.onclick = () => {
    document.querySelectorAll(".tabs button").forEach((x) => x.classList.remove("active"));
    document.querySelectorAll(".tab").forEach((x) => x.classList.remove("active"));
    b.classList.add("active");
    $("tab-" + b.dataset.tab).classList.add("active");
    if (b.dataset.tab === "evidence") loadEvidence();
  };
});

// ---------- evidence ----------
$("btn-upload").onclick = async () => {
  const f = $("ev-file").files[0];
  if (!f) return;
  const fd = new FormData();
  fd.append("file", f);
  try {
    const r = await api("POST", "/api/evidence", fd, true);
    $("ev-msg").textContent = (r.duplicate ? "已存在（去重）：" : "已接收不可变证据：") + r.evidenceId;
    loadEvidence();
  } catch (e) { $("ev-msg").textContent = "上传失败：" + e.message; }
};

async function loadEvidence() {
  const list = await api("GET", "/api/evidence");
  $("ev-rows").innerHTML = "";
  for (const e of list) {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td class="code">${e.evidenceId}</td><td>${e.originalFilename}</td>
      <td>${e.width}×${e.height}</td><td class="code" title="${e.contentSha256}">${e.contentSha256.slice(0, 18)}…</td>
      <td>${new Date(e.receivedAt).toLocaleString()}</td>
      <td><a href="/api/evidence/${e.evidenceId}/raw" target="_blank">查看</a></td>`;
    $("ev-rows").appendChild(tr);
  }
}

// ---------- new composition ----------
$("btn-new").onclick = async () => {
  $("new-panel").classList.remove("hidden");
  $("cmp-panel").classList.add("hidden");
  await renderFrameRows();
};
$("btn-cancel-new").onclick = () => $("new-panel").classList.add("hidden");
$("btn-add-frame-row").onclick = () => addFrameRow();

async function renderFrameRows() {
  const evidence = await api("GET", "/api/evidence");
  const tb = $("new-frames").querySelector("tbody");
  tb.innerHTML = "";
  addFrameRow(evidence);
}

function addFrameRow(prefill) {
  const tb = $("new-frames").querySelector("tbody");
  const tr = document.createElement("tr");
  tr.innerHTML = `<td><select class="f-ev"></select></td>
    <td><input class="f-mag" placeholder="如 10，留空=缺失" /></td>
    <td><input class="f-sx" placeholder="数字" /></td>
    <td><input class="f-sxu" placeholder="um" /></td>
    <td><input class="f-sy" placeholder="数字" /></td>
    <td><input class="f-syu" placeholder="um" /></td>
    <td><input class="f-z" value="0" /></td>
    <td><input class="f-time" placeholder="ISO8601 可选" /></td>
    <td><button class="danger">删除</button></td>`;
  tb.appendChild(tr);
  tr.querySelector("button").onclick = () => tr.remove();
  const sel = tr.querySelector(".f-ev");
  for (const e of prefill || []) {
    const o = document.createElement("option");
    o.value = e.evidenceId;
    o.textContent = `${e.evidenceId} · ${e.originalFilename} (${e.width}×${e.height})`;
    sel.appendChild(o);
  }
}

$("btn-create").onclick = async () => {
  const frames = [];
  for (const tr of $("new-frames").querySelectorAll("tbody tr")) {
    const get = (cls) => tr.querySelector(cls).value.trim();
    const mag = get(".f-mag");
    frames.push({
      evidenceId: get(".f-ev"),
      magnification: mag === "" ? null : Number(mag),
      stageX: get(".f-sx") === "" ? null : Number(get(".f-sx")),
      stageY: get(".f-sy") === "" ? null : Number(get(".f-sy")),
      stageXUnit: get(".f-sxu") || null,
      stageYUnit: get(".f-syu") || null,
      zHeightUm: Number(get(".f-z") || "0"),
      capturedAt: get(".f-time") || null,
    });
  }
  if (frames.some((f) => !f.evidenceId)) { $("new-error").textContent = "每行都要选择证据"; return; }
  try {
    const detail = await api("POST", "/api/compositions", {
      name: $("new-name").value.trim() || null,
      frames,
      note: "initial candidate",
    });
    $("new-panel").classList.add("hidden");
    await loadCompositions(detail.summary.id, detail.versions[0].versionId);
  } catch (e) { $("new-error").textContent = "创建失败：" + e.message; }
};

$("btn-demo").onclick = async () => {
  const detail = await api("POST", "/api/demo");
  await loadCompositions(detail.summary.id, detail.versions[0].versionId);
};

// ---------- composition list ----------
$("btn-reload").onclick = () => loadCompositions();
$("combo-compositions").onchange = (e) => loadCompositions(e.target.value);

async function loadCompositions(selectId, versionId) {
  state.compositions = await api("GET", "/api/compositions");
  const sel = $("combo-compositions");
  sel.innerHTML = "";
  for (const c of state.compositions) {
    const o = document.createElement("option");
    o.value = c.id;
    o.textContent = `${c.name} · ${c.frameCount}帧 · rev ${c.revision}${c.publishedVersionId ? " · 已发布" : ""}`;
    sel.appendChild(o);
  }
  const id = selectId || state.currentId || state.compositions[0]?.id;
  if (!id) { $("cmp-panel").classList.add("hidden"); return; }
  sel.value = id;
  await openComposition(id, versionId);
}

// ---------- composition view ----------
async function openComposition(id, versionId) {
  state.currentId = id;
  state.detail = await api("GET", `/api/compositions/${id}`);
  $("cmp-panel").classList.remove("hidden");
  const d = state.detail;
  $("cmp-name").textContent = d.summary.name;
  $("cmp-meta").textContent =
    `${d.summary.id} · 创建 ${new Date(d.summary.createdAt).toLocaleString()} · rev ${d.summary.revision} · ` +
    (d.summary.publishedVersionId ? `已发布 ${d.summary.publishedVersionId}` : "尚无发布版本");

  const vs = $("combo-versions");
  vs.innerHTML = "";
  for (const v of d.versions) {
    const o = document.createElement("option");
    o.value = v.versionId;
    o.textContent = `${v.versionId} ${v.isPublished ? "★已发布" : "候选"} — ${v.note}`;
    vs.appendChild(o);
  }
  vs.value = versionId || d.summary.publishedVersionId || d.summary.latestVersionId;
  state.versionId = vs.value;
  vs.onchange = () => selectVersion(vs.value);
  await selectVersion(state.versionId);
}

async function selectVersion(versionId) {
  state.versionId = versionId;
  const v = state.detail.versions.find((x) => x.versionId === versionId);
  state.selectedTile = null;
  state.pendingOps = [];
  state.editingBase = versionId;
  $("merge-panel").classList.add("hidden");
  $("version-meta").textContent =
    `父版本 ${v.parentVersionId || "（根）"} · 规则 ${v.ruleSetVersion} · 参考倍率 ${v.referenceMagnification}x · ` +
    `${v.canvasWidth}×${v.canvasHeight}px · 创建 ${new Date(v.createdAt).toLocaleString()}`;
  renderFrames(v);
  await drawVersion(v);
  renderTileDetail(v, null);
  $("btn-publish").textContent = v.isPublished ? "重新发布此版本（原子切换）" : "发布此版本（需全部瓦片验证）";
  $("btn-publish").classList.toggle("primary", !v.isPublished);
}

function renderFrames(v) {
  const ul = $("frame-list");
  ul.innerHTML = "";
  for (const f of v.frames) {
    const diag = v.diagnostics.find((d) => d.evidenceId === f.evidenceId);
    const bad = diag && diag.code !== "PlacementOk";
    const li = document.createElement("li");
    li.innerHTML = `<div><b>${f.originalFilename}</b> <span class="code">${f.evidenceId}</span></div>
      <div>指纹 <span class="code" title="${f.contentSha256}">${f.contentSha256.slice(0, 16)}…</span> ·
      ${f.magnificationMissing ? "<b>倍率缺失</b>" : f.magnification + "x"} ·
      Z ${f.zHeightUm}µm · ${f.width}×${f.height}</div>
      <div>载台 (${f.stageX ?? "?"}, ${f.stageY ?? "?"}) 单位 '${f.stageXUnit || "缺失"}'/'${f.stageYUnit || "缺失"}'</div>
      <div><span class="code ${bad ? "bad" : "ok"}">${diag?.code || "unknown"}</span> ${diag?.message || ""}</div>`;
    ul.appendChild(li);
  }
}

async function loadArtifact(kind) {
  const url = `/api/compositions/${state.currentId}/versions/${state.versionId}/artifacts/${kind}`;
  const blob = await (await fetch(url)).blob();
  return await createImageBitmap(blob);
}

async function drawVersion(v) {
  const maxW = Math.min(760, window.innerWidth - 500);
  state.scale = Math.max(1, Math.min(3, Math.floor(maxW / v.canvasWidth)));
  const W = v.canvasWidth * state.scale, H = v.canvasHeight * state.scale;
  for (const id of ["cv-composite", "cv-overlay", "cv-tiles"]) {
    const cv = $(id);
    cv.width = W; cv.height = H;
    cv.style.width = W + "px"; cv.style.height = H + "px";
  }
  const comp = await loadArtifact("composite");
  const ctx = $("cv-composite").getContext("2d");
  ctx.imageSmoothingEnabled = false;
  ctx.clearRect(0, 0, W, H);
  ctx.drawImage(comp, 0, 0, W, H);

  const overlay = await loadArtifact("overlay");
  const octx = $("cv-overlay").getContext("2d");
  octx.clearRect(0, 0, W, H);
  const ib = await createImageBitmap(overlay);
  octx.globalAlpha = 0.55;
  octx.imageSmoothingEnabled = false;
  // recolor grayscale diagnostic codes
  const tmp = document.createElement("canvas");
  tmp.width = v.canvasWidth; tmp.height = v.canvasHeight;
  const tctx = tmp.getContext("2d");
  tctx.drawImage(ib, 0, 0);
  const imgData = tctx.getImageData(0, 0, v.canvasWidth, v.canvasHeight);
  const colors = { 1: [90, 90, 90], 2: [181, 138, 0], 3: [43, 127, 184], 4: [192, 57, 43], 5: [142, 68, 173] };
  for (let i = 0; i < imgData.data.length; i += 4) {
    const c = colors[imgData.data[i]];
    if (c) { imgData.data[i] = c[0]; imgData.data[i + 1] = c[1]; imgData.data[i + 2] = c[2]; imgData.data[i + 3] = 150; }
    else { imgData.data[i + 3] = 0; }
  }
  tctx.putImageData(imgData, 0, 0);
  octx.drawImage(tmp, 0, 0, W, H);
  drawTiles(v);
}

function drawTiles(v) {
  const ctx = $("cv-tiles").getContext("2d");
  const s = state.scale;
  ctx.clearRect(0, 0, $("cv-tiles").width, $("cv-tiles").height);
  ctx.lineWidth = 1;
  for (const t of v.tiles) {
    ctx.strokeStyle = t.validated ? "#1e8449" : "rgba(255,255,255,0.5)";
    ctx.setLineDash(t.validated ? [] : [3, 3]);
    ctx.strokeRect(t.x * s + 0.5, t.y * s + 0.5, t.width * s - 1, t.height * s - 1);
  }
  if (state.selectedTile != null) {
    const t = v.tiles[state.selectedTile];
    ctx.setLineDash([]);
    ctx.strokeStyle = "#ffd34d";
    ctx.lineWidth = 2;
    ctx.strokeRect(t.x * s + 1, t.y * s + 1, t.width * s - 2, t.height * s - 2);
  }
}

$("cv-tiles").onclick = (e) => {
  const v = state.detail.versions.find((x) => x.versionId === state.versionId);
  const rect = e.target.getBoundingClientRect();
  const x = Math.floor((e.clientX - rect.left) / state.scale);
  const y = Math.floor((e.clientY - rect.top) / state.scale);
  const t = v.tiles.find((t) => x >= t.x && x < t.x + t.width && y >= t.y && y < t.y + t.height);
  if (t) { state.selectedTile = t.index; drawTiles(v); renderTileDetail(v, t.index); }
};

["layer-composite", "layer-overlay", "layer-tiles"].forEach((id) => {
  $(id).onchange = () => {
    $("cv-composite").style.display = $("layer-composite").checked ? "" : "none";
    $("cv-overlay").style.display = $("layer-overlay").checked ? "" : "none";
    $("cv-tiles").style.display = $("layer-tiles").checked ? "" : "none";
  };
});

// ---------- tile detail & manual operations ----------
function renderTileDetail(v, idx) {
  $("tile-title").textContent = idx == null ? "" : `#${idx}`;
  const box = $("tile-detail");
  if (idx == null) {
    box.innerHTML = '<p class="hint">点击画布上的瓦片查看每个来源的清晰度分数、改选来源或排除带尘点的帧。</p>';
    return;
  }
  const t = v.tiles[idx];
  const maxScore = Math.max(1e-9, ...t.scores.map((s) => s.meanSharpness));
  const rows = v.frames.map((f) => {
    const score = t.scores.find((s) => s.evidenceId === f.evidenceId);
    const pct = score ? Math.round((score.meanSharpness / maxScore) * 100) : 0;
    const excluded = t.excludedEvidenceIds.includes(f.evidenceId);
    const forced = t.forcedEvidenceId === f.evidenceId;
    const selected = t.selectedEvidenceId === f.evidenceId;
    return `<div class="tile-row">
      <label title="${f.originalFilename}">
        <input type="radio" name="force" data-ev="${f.evidenceId}" ${forced ? "checked" : ""}/>
        <input type="checkbox" class="ex" data-ev="${f.evidenceId}" ${excluded ? "checked" : ""}/>
        <span class="code">${f.evidenceId.slice(-6)}</span>
        ${selected ? "★" : ""} ${f.originalFilename}
      </label>
      <span class="scorebar"><i style="width:${pct}%"></i></span>
      <span class="code">${score ? score.meanSharpness.toFixed(1) : "—"}</span>
    </div>`;
  }).join("");

  const untrusted = Object.entries(t.untrustedPixels)
    .filter(([, n]) => n > 0)
    .map(([k, n]) => `${k}: ${n}px`).join("，") || "无";
  box.innerHTML = `
    <div class="hint">自动选择：<span class="code">${t.selectedEvidenceId || "无（不可信）"}</span>；
      可信 ${t.trustedPixels}px；不可信：${untrusted}</div>
    <div>单选 = 强制来源；勾选 = 排除尘帧（仅作用于本瓦片）</div>
    ${rows}
    <div class="toolbar">
      <button id="btn-save-tile">保存瓦片改选</button>
      <button id="btn-validate">${t.validated ? "撤销验证" : "标记此瓦片已验证"}</button>
    </div>`;

  box.querySelector("#btn-validate").onclick = async () => {
    const updated = await api("POST",
      `/api/compositions/${state.currentId}/versions/${v.versionId}/tiles/validate`,
      { tileIndex: idx, validated: !t.validated });
    replaceVersion(updated);
  };

  box.querySelector("#btn-save-tile").onclick = async () => {
    const excluded = [...box.querySelectorAll(".ex:checked")].map((c) => c.dataset.ev);
    const forced = box.querySelector("input[name=force]:checked")?.dataset.ev ?? null;
    await submitOperations([{ tileIndex: idx, kind: "replace", excludedEvidenceIds: excluded, forcedEvidenceId: forced }]);
  };
}

function replaceVersion(updated) {
  const i = state.detail.versions.findIndex((x) => x.versionId === updated.versionId);
  if (i >= 0) state.detail.versions[i] = updated; else state.detail.versions.push(updated);
  state.versionId = updated.versionId;
  $("combo-versions").querySelectorAll("option").forEach((o) => {
    if (o.value === updated.versionId) o.textContent = `${updated.versionId} ${updated.isPublished ? "★已发布" : "候选"} — ${updated.note}`;
  });
  selectVersion(updated.versionId);
}

async function submitOperations(operations, isMerged) {
  try {
    const updated = await api("POST", `/api/compositions/${state.currentId}/versions`, {
      baseVersionId: state.editingBase,
      note: isMerged ? "manual re-merge" : "manual tile edit",
      operations,
    });
    await refreshDetail(updated.versionId);
    $("merge-panel").classList.add("hidden");
  } catch (e) {
    if (e.status === 409) showConflict(e.payload, operations);
    else $("publish-error").textContent = "提交失败：" + e.message;
  }
}

async function refreshDetail(versionId) {
  await openComposition(state.currentId, versionId);
}

function showConflict(payload, clientOps) {
  state.conflict = { payload, clientOps };
  $("merge-panel").classList.remove("hidden");
  const list = $("conflict-list");
  list.innerHTML = "";
  for (const c of payload.conflicts) {
    const div = document.createElement("div");
    div.className = "conflict-tile";
    div.innerHTML = `<b>瓦片 #${c.tileIndex}</b>
      <label><input type="radio" name="cf-${c.tileIndex}" value="server" checked/>
        服务器 ${c.serverVersionId.slice(-6)}：强制 ${c.serverForcedEvidenceId?.slice(-6) || "自动"}，
        排除 ${c.serverExcludedEvidenceIds.map((x) => x.slice(-6)).join(",") || "无"}</label>
      <label><input type="radio" name="cf-${c.tileIndex}" value="client"/>
        我的改动（基于 ${c.clientBaseVersionId.slice(-6)}）：强制 ${c.clientForcedEvidenceId?.slice(-6) || "自动"}，
        排除 ${c.clientExcludedEvidenceIds.map((x) => x.slice(-6)).join(",") || "无"}</label>`;
    list.appendChild(div);
  }
}

$("btn-merge-submit").onclick = async () => {
  const { payload, clientOps } = state.conflict;
  const byTile = Object.fromEntries(clientOps.map((o) => [o.tileIndex, o]));
  const merged = [];
  for (const c of payload.conflicts) {
    const choice = document.querySelector(`input[name=cf-${c.tileIndex}]:checked`).value;
    if (choice === "client") merged.push(byTile[c.tileIndex]);
    // server choice -> keep server state: no operation for that tile
  }
  // also include non-conflicting client ops (disjoint tiles auto-merge server-side anyway)
  const conflictIdx = new Set(payload.conflicts.map((c) => c.tileIndex));
  for (const op of clientOps) if (!conflictIdx.has(op.tileIndex)) merged.push(op);
  state.editingBase = payload.currentVersionId;
  await submitOperations(merged, true);
};

$("btn-new-candidate").onclick = () => {
  state.editingBase = state.versionId;
  alert("当前版本已作为编辑基线。点击任一瓦片，修改来源后「保存瓦片改选」即生成新候选版本。");
};

$("btn-cancel-version").onclick = async () => {
  const v = state.detail.versions.find((x) => x.versionId === state.versionId);
  if (v.isPublished) { alert("已发布版本不可取消。"); return; }
  if (!confirm("放弃该未发布候选？已发布版本不受影响。")) return;
  await api("DELETE", `/api/compositions/${state.currentId}/versions/${state.versionId}`);
  await refreshDetail(state.detail.summary.publishedVersionId || null);
};

// ---------- publish ----------
$("btn-publish").onclick = async () => {
  $("publish-error").textContent = "";
  try {
    const r = await api("POST", `/api/compositions/${state.currentId}/publish`, { versionId: state.versionId });
    await refreshDetail(r.publishedVersionId);
  } catch (e) {
    if (e.payload?.error === "tiles-unvalidated")
      $("publish-error").textContent = `还有 ${e.payload.unvalidatedTiles.length} 个瓦片未验证：#${e.payload.unvalidatedTiles.join(", #")}`;
    else $("publish-error").textContent = "发布失败：" + e.message;
  }
};

// ---------- export ----------
$("btn-export").onclick = () => {
  window.location = `/api/compositions/${state.currentId}/export/${state.versionId}`;
};

// ---------- import ----------
$("btn-import").onclick = async () => {
  const f = $("import-file").files[0];
  if (!f) return;
  const fd = new FormData();
  fd.append("package", f);
  const res = await fetch("/api/imports/verify", { method: "POST", body: fd });
  const report = await res.json();
  $("import-report").textContent = JSON.stringify(report, null, 2);
};

// ---------- boot ----------
loadCompositions();
