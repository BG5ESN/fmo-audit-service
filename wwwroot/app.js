/* EMQX 审计监控 (v2) — 前端逻辑：排行榜 / 健康 / 配置 */
(function () {
  'use strict';

  const $ = id => document.getElementById(id);
  const page = location.pathname;

  // ---------------- 通用 ----------------

  async function api(path, opts) {
    const r = await fetch(path, opts);
    if (r.status === 401) { location.href = '/login.html'; throw new Error('未登录'); }
    return r.json();
  }

    function fmtBytes(n) {
    if (n >= 1e9) return (n / 1e9).toFixed(2) + ' GB';
    if (n >= 1e6) return (n / 1e6).toFixed(2) + ' MB';
    if (n >= 1e3) return (n / 1e3).toFixed(1) + ' KB';
    return n + ' B';
  }
  function fmtNum(n) { return Number(n).toLocaleString('en-US'); }
  function esc(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }
  // 用户显示名：呼号（UID），无 uid 时只显示呼号
  function showName(name, uid) {
    return uid ? `${esc(name)}（${esc(uid)}）` : esc(name);
  }

  async function refreshStatus() {
    try {
      const d = await api('/api/status');
      const el = $('collect-status');
      if (!el) return;
      if (d.collecting) {
        el.className = d.last_collect_ok ? 'status-ok' : 'status-err';
        el.textContent = d.last_status || '采集中…';
      } else {
        el.className = '';
        el.textContent = '未连接 EMQX';
      }
    } catch (e) { /* 401 已处理 */ }
  }

  // 首次使用引导：未完成（未配置 EMQX）时强制跳配置页
  async function ensureWizard() {
    try {
      const d = await api('/api/status');
      if (!d.wizard_done && !d.configured) {
        location.href = '/settings.html';
        return false;
      }
      return true;
    } catch (e) { return false; }
  }

  $('logout').addEventListener('click', async () => {
    await fetch('/api/logout', { method: 'POST' });
    location.href = '/login.html';
  });

  // ---------------- 黑名单（排行榜 / 主题 / 黑名单页共用） ----------------

  let blMap = null, blMapAt = 0;

  // 当前生效黑名单（本地推导 + EMQX 侧对照），60s 缓存
  async function getBlacklistActive(force) {
    const now = Date.now();
    if (!force && blMap && now - blMapAt < 60000) return blMap;
    try {
      const d = await api('/api/blacklist/active');
      const map = {};
      (d.local || []).forEach(x => { map[x.who] = { who: x.who, reason: x.reason || '', until: x.until, operator: x.operator, createdAt: x.createdAt, src: 'local' }; });
      (d.emqx_only || []).forEach(x => { if (!map[x.who]) map[x.who] = { who: x.who, reason: x.reason || '', until: x.until, operator: '', createdAt: '', src: 'emqx' }; });
      blMap = map; blMapAt = now;
      return map;
    } catch (e) { return blMap || {}; }
  }
  function blInvalidate() { blMap = null; }

  // 操作列 HTML：匿名客户端（无呼号）不提供拉黑
  function banCellHtml(r) {
    if (r.isAnonymous) return '<span class="ban-note">匿名</span>';
    const b = blMap && blMap[r.name];
    return b
      ? `<button class="btn btn-small ban-btn banned" data-unban="${esc(r.name)}">解封</button>`
      : `<button class="btn btn-small ban-btn" data-ban="${esc(r.name)}">拉黑</button>`;
  }

  // 表格操作列事件委托（渲染后调用一次即可）
  function bindBanActions(tbody) {
    tbody.onclick = e => {
      const un = e.target.closest('[data-unban]');
      const bn = e.target.closest('[data-ban]');
      if (un) { doUnban(un.dataset.unban); return; }
      if (bn) { openBanModal(bn.dataset.ban); return; }
    };
  }

  // 拉黑弹窗：who 为空 = 手动输入呼号
  function openBanModal(who) {
    const root = $('modal-root');
    if (!root) return;
    const needInput = !who;
    root.innerHTML = `
      <div class="modal-mask">
        <div class="modal">
          <div class="modal-title">${needInput ? '手动拉黑呼号' : `拉黑呼号 ${esc(who)}`}</div>
          <div class="modal-body">
            ${needInput ? '<div class="form-row"><label>呼号（username）</label><input class="text-input" id="ban-who" placeholder="如 BG5ABC"></div>' : ''}
            <label>原因（留痕，建议填写）</label>
            <textarea id="ban-reason" placeholder="如：伪造数据包干扰信道"></textarea>
            <div style="margin-top:12px">
              <label>封禁时长（到期自动解除）</label>
              <div class="dur-row">
                <label><input type="radio" name="ban-dur" value="" checked> 永久</label>
                <label><input type="radio" name="ban-dur" value="1"> 1小时</label>
                <label><input type="radio" name="ban-dur" value="6"> 6小时</label>
                <label><input type="radio" name="ban-dur" value="24"> 24小时</label>
                <label><input type="radio" name="ban-dur" value="custom"> 自定义</label>
                <input type="datetime-local" id="ban-until" class="hidden">
              </div>
            </div>
            <div id="ban-msg" class="form-msg"></div>
          </div>
          <div class="modal-footer">
            <button class="btn" id="ban-cancel">取消</button>
            <button class="btn btn-primary" id="ban-ok">拉黑并踢下线</button>
          </div>
        </div>
      </div>`;
    const mask = root.querySelector('.modal-mask');
    const msg = $('ban-msg');
    const untilInput = $('ban-until');
    root.querySelectorAll('input[name="ban-dur"]').forEach(r => {
      r.onchange = () => { untilInput.classList.toggle('hidden', r.value !== 'custom'); };
    });
    $('ban-cancel').onclick = () => { root.innerHTML = ''; };
    mask.onclick = e => { if (e.target === mask) root.innerHTML = ''; };
    $('ban-ok').onclick = async () => {
      const who2 = needInput ? $('ban-who').value.trim() : who;
      const msg2 = msg;
      msg2.className = 'form-msg err';
      if (!who2) { msg2.textContent = '请输入呼号'; return; }
      const reason = $('ban-reason').value.trim();
      const dur = root.querySelector('input[name="ban-dur"]:checked').value;
      let until = null;
      if (dur === 'custom') {
        until = $('ban-until').value;
        if (!until) { msg2.textContent = '请选择自定义到期时间'; return; }
      } else if (dur) {
        const d = new Date(Date.now() + dur * 3600 * 1000);
        const pad = n => String(n).padStart(2, '0');
        until = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
      }
      $('ban-ok').disabled = true;
      msg2.className = 'form-msg';
      msg2.textContent = '正在拉黑…';
      try {
        const d = await api('/api/blacklist/ban', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ who: who2, reason, until })
        });
        if (d.ok) {
          msg2.className = 'form-msg ok';
          msg2.textContent = d.kicked > 0 ? `已拉黑 ${d.who}，踢下线 ${d.kicked} 个在线客户端` : `已拉黑 ${d.who}（当前无在线客户端）`;
          blInvalidate();
          setTimeout(() => { root.innerHTML = ''; refreshAfterBl(); }, 1200);
        } else {
          msg2.className = 'form-msg err';
          msg2.textContent = d.error || '拉黑失败';
          $('ban-ok').disabled = false;
        }
      } catch (e) { /* 401 已处理 */ }
    };
  }

  async function doUnban(who) {
    if (!confirm(`确认解封 ${who}？解封后该呼号可重新连接 EMQX。`)) return;
    try {
      const d = await api('/api/blacklist/unban', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ who })
      });
      if (d.ok) { blInvalidate(); refreshAfterBl(); }
      else alert(d.error || '解封失败');
    } catch (e) { /* 401 已处理 */ }
  }

  // 黑名单操作后刷新当前页（各页面在 init 时赋值）
  let refreshAfterBl = () => {};

  // ---------------- 排行榜页 ----------------

  if (page === '/' || page === '/index.html') { initLeaderboard(); }

  function initLeaderboard() {
    let range = 'custom', order = 'oct';

    // 默认：今天 0:00 - 现在
    const pad = n => String(n).padStart(2, '0');
    const now = new Date();
    const today0 = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T00:00`;
    const nowStr = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}`;
    $('from').value = today0;
    $('to').value = nowStr;

    function quickRange(r) {
      const d = new Date();
      const fmt = dt => `${dt.getFullYear()}-${pad(dt.getMonth() + 1)}-${pad(dt.getDate())}T${pad(dt.getHours())}:${pad(dt.getMinutes())}`;
      if (r === 'today') {
        $('from').value = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T00:00`;
        $('to').value = fmt(d);
      } else if (r === 'yesterday') {
        const y = new Date(d); y.setDate(d.getDate() - 1);
        $('from').value = `${y.getFullYear()}-${pad(y.getMonth() + 1)}-${pad(y.getDate())}T00:00`;
        $('to').value = `${y.getFullYear()}-${pad(y.getMonth() + 1)}-${pad(y.getDate())}T23:59`;
      } else if (r === '7d' || r === '30d') {
        const days = r === '7d' ? 7 : 30;
        const s = new Date(d); s.setDate(d.getDate() - days);
        $('from').value = fmt(s);
        $('to').value = fmt(d);
      }
    }

    document.querySelectorAll('.filter-bar .chip[data-range]').forEach(b => {
      b.onclick = () => {
        document.querySelectorAll('.filter-bar .chip[data-range]').forEach(x => x.classList.remove('active'));
        b.classList.add('active');
        range = b.dataset.range;
        if (range !== 'custom') quickRange(range);
      };
    });
    document.querySelectorAll('.filter-bar .chip[data-order]').forEach(b => {
      b.onclick = () => {
        document.querySelectorAll('.filter-bar .chip[data-order]').forEach(x => x.classList.remove('active'));
        b.classList.add('active');
        order = b.dataset.order;
        query();
      };
    });

    $('query').onclick = query;
    $('export').onclick = () => {
      const f = $('from').value, t = $('to').value;
      if (!f || !t) return;
      location.href = `/api/export.csv?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}&order=${order}`;
    };

    async function query() {
      const f = $('from').value, t = $('to').value;
      if (!f || !t) { alert('请选择起止时间'); return; }
      $('query').disabled = true;
      const started = Date.now();
      try {
        const d = await api(`/api/leaderboard?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}&order=${order}&limit=200`);
        if (!d.ok) { alert(d.error || '查询失败'); return; }
        await getBlacklistActive();
        $('range-desc').textContent = `${d.from.replace('T', ' ')} 至 ${d.to.replace('T', ' ')}`;
        $('total-rows').textContent = d.rows.length ? `共 ${d.rows.length} 个呼号` : '';
        $('query-time').textContent = `查询耗时 ${Date.now() - started}ms`;
        render(d.rows, order);
      } catch (e) { /* 401 已处理 */ }
      finally { $('query').disabled = false; }
    }

    function render(rows, ord) {
      const tbody = $('rows');
      tbody.innerHTML = '';
      $('empty').classList.toggle('hidden', rows.length > 0);
      rows.forEach((r, i) => {
        const tr = document.createElement('tr');
        const rankCls = i === 0 ? 'rank-top1' : i === 1 ? 'rank-top2' : i === 2 ? 'rank-top3' : '';
        const banned = blMap && blMap[r.name];
        tr.innerHTML = `
          <td class="num"><span class="${rankCls}">${i + 1}</span></td>
          <td><a class="name-cell" data-name="${esc(r.name)}">${showName(r.name, r.uid)}${banned ? '<span class="ban-badge">已拉黑</span>' : ''}</a></td>
          <td class="num">${r.deviceCount}</td>
          <td class="num">${fmtBytes(r.totalOct)}</td>
          <td class="num">${fmtNum(r.totalMsg)}</td>
          <td class="num">${fmtNum(r.totalPkt)}</td>
          <td class="num">${r.reconnectCount > 0 ? '<span class="reconnect-badge">' + r.reconnectCount + '</span>' : 0}</td>
          <td>${banCellHtml(r)}</td>`;
        tr.querySelector('.name-cell').onclick = () => toggleDetail(tr, r.name, ord);
        tbody.appendChild(tr);
      });
    }

    let openRow = null;
    async function toggleDetail(tr, name, ord) {
      if (openRow && openRow.parentNode === tr.nextSibling) {
        openRow.remove(); openRow = null; return;
      }
      if (openRow) openRow.remove();
      const f = $('from').value, t = $('to').value;
      const d = await api(`/api/leaderboard/${encodeURIComponent(name)}?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}`);
      const detail = document.createElement('tr');
      detail.className = 'detail-row';
      detail.innerHTML = `<td colspan="8"><div class="detail-box">
        <h4>呼号 ${esc(name)} — clientid 明细（${d.rows.length} 行）</h4>
        <table class="detail-table">
          <thead><tr><th>clientid</th><th class="num">发送字节</th><th class="num">接收字节</th><th class="num">发送消息</th><th class="num">接收消息</th><th class="num">发送包</th><th class="num">接收包</th><th>重连</th></tr></thead>
          <tbody></tbody>
        </table></div></td>`;
      const tb = detail.querySelector('tbody');
      // 按 clientid 聚合
      const byCid = {};
      d.rows.forEach(r => {
        if (!byCid[r.clientId]) byCid[r.clientId] = { cid: r.clientId, uid: r.uid, so: 0, ro: 0, sm: 0, rm: 0, sp: 0, rp: 0, rc: 0, ip: r.ipAddress };
        const g = byCid[r.clientId];
        g.so += r.sendOct; g.ro += r.recvOct; g.sm += r.sendMsg; g.rm += r.recvMsg; g.sp += r.sendPkt; g.rp += r.recvPkt; g.rc += r.reconnect ? 1 : 0;
      });
      Object.values(byCid).sort((a, b) => (b.so + b.ro) - (a.so + a.ro)).forEach(g => {
        const tr2 = document.createElement('tr');
        tr2.innerHTML = `<td class="mono">${showName(g.cid, g.uid)}</td><td class="num">${fmtBytes(g.so)}</td><td class="num">${fmtBytes(g.ro)}</td>
          <td class="num">${fmtNum(g.sm)}</td><td class="num">${fmtNum(g.rm)}</td><td class="num">${fmtNum(g.sp)}</td><td class="num">${fmtNum(g.rp)}</td>
          <td>${g.rc > 0 ? '<span class="reconnect-badge">' + g.rc + '</span>' : ''}</td>`;
        tb.appendChild(tr2);
      });
      tr.after(detail);
      openRow = detail;
    }

    query();
    ensureWizard();
    refreshStatus();
    setInterval(refreshStatus, 30000);
    refreshAfterBl = query;
    bindBanActions($('rows'));
    // 排行榜自动刷新（30 秒；勾选"自动刷新"才刷，页面隐藏时不刷）
    setInterval(() => {
      if (document.hidden) return;
      if (!$('auto-refresh') || !$('auto-refresh').checked) return;
      query();
    }, 30000);
  }

  // ---------------- 健康页 ----------------

  if (page === '/health.html') { initHealth(); }

  function initHealth() {
    let range = '7d';
    const pad = n => String(n).padStart(2, '0');
    const now = new Date();
    const fmt = dt => `${dt.getFullYear()}-${pad(dt.getMonth() + 1)}-${pad(dt.getDate())}T${pad(dt.getHours())}:${pad(dt.getMinutes())}`;
    const s7 = new Date(now); s7.setDate(now.getDate() - 7);
    $('from').value = fmt(s7);
    $('to').value = fmt(now);

    document.querySelectorAll('.filter-bar .chip[data-range]').forEach(b => {
      b.onclick = () => {
        document.querySelectorAll('.filter-bar .chip[data-range]').forEach(x => x.classList.remove('active'));
        b.classList.add('active');
        range = b.dataset.range;
        if (range === 'today') { $('from').value = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T00:00`; $('to').value = fmt(now); }
        else if (range === 'yesterday') { const y = new Date(now); y.setDate(now.getDate() - 1); $('from').value = `${y.getFullYear()}-${pad(y.getMonth() + 1)}-${pad(y.getDate())}T00:00`; $('to').value = `${y.getFullYear()}-${pad(y.getMonth() + 1)}-${pad(y.getDate())}T23:59`; }
        else if (range === '30d') { const s = new Date(now); s.setDate(now.getDate() - 30); $('from').value = fmt(s); $('to').value = fmt(now); }
        else { const s = new Date(now); s.setDate(now.getDate() - 7); $('from').value = fmt(s); $('to').value = fmt(now); }
        query();
      };
    });
    $('query').onclick = query;

    async function query() {
      const f = $('from').value, t = $('to').value;
      const d = await api(`/api/health?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}`);
      if (!d.ok) { alert(d.error || '查询失败'); return; }
      drawHealth(d.rows);
    }

    function drawHealth(rows) {
      const ts = rows.map(r => r.ts);
      // 宿主机 CPU/内存/磁盘
      drawLine('host-cpu', 'legend-host', ts, [
        { name: 'CPU %', color: '#d32f2f', data: rows.map(r => r.hostCpuPct) },
        { name: '内存 %', color: '#1565c0', data: rows.map(r => r.hostMemUsedPct) },
        { name: '磁盘 %', color: '#2e7d32', data: rows.map(r => r.hostDiskUsedPct) },
      ], '%');
      // 网络
      drawLine('host-net', 'legend-net', ts, [
        { name: '下行 KB/s', color: '#1565c0', data: rows.map(r => r.hostNetRecvKbps) },
        { name: '上行 KB/s', color: '#e65100', data: rows.map(r => r.hostNetSendKbps) },
      ], 'KB/s');
      // EMQX 连接数/消息速率
      drawLine('emqx-basic', 'legend-emqx', ts, [
        { name: '连接数', color: '#2e7d32', data: rows.map(r => r.emqxConnections) },
        { name: '消息速率 条/s', color: '#6a1b9a', data: rows.map(r => r.emqxMsgRate) },
        { name: '节点负载 load1', color: '#e65100', data: rows.map(r => r.emqxCpuPct) },
      ], '');
      // 告警
      const alarms = rows.map(r => r.emqxAlarms).filter(Boolean);
      const uniq = [...new Set(alarms.flatMap(a => a.split(/,\s*/)))];
      if (uniq.length) {
        $('alarm-card').style.display = '';
        $('alarm-list').textContent = uniq.join('；');
      } else {
        $('alarm-card').style.display = 'none';
      }
    }

    // 简化 Canvas 折线（无 hover；时间轴稀疏标签）
    function drawLine(canvasId, legendId, ts, series, unit) {
      const cv = $(canvasId);
      const legend = $(legendId);
      legend.innerHTML = series.filter(s => s.data.some(v => v != null))
        .map(s => `<span class="legend-item"><span class="legend-swatch" style="background:${s.color}"></span>${s.name}</span>`).join('');
      const dpr = window.devicePixelRatio || 1;
      const W = cv.clientWidth, H = cv.clientHeight;
      cv.width = W * dpr; cv.height = H * dpr;
      const ctx = cv.getContext('2d');
      ctx.scale(dpr, dpr);
      ctx.clearRect(0, 0, W, H);

      const padL = 46, padR = 10, padT = 8, padB = 22;
      const iw = W - padL - padR, ih = H - padT - padB;

      // 数据范围
      const all = series.flatMap(s => s.data).filter(v => v != null);
      let max = all.length ? Math.max(...all) : 0;
      if (max <= 0) max = 1;
      max = max * 1.1;

      // 网格 + Y 轴
      ctx.strokeStyle = '#e5e5e5';
      ctx.fillStyle = '#999';
      ctx.font = '10px sans-serif';
      ctx.lineWidth = 1;
      for (let i = 0; i <= 4; i++) {
        const y = padT + ih * i / 4;
        ctx.beginPath(); ctx.moveTo(padL, y); ctx.lineTo(W - padR, y); ctx.stroke();
        const val = max * (1 - i / 4);
        ctx.textAlign = 'right';
        ctx.fillText(val >= 1000 ? (val / 1000).toFixed(1) + 'k' : Math.round(val).toString(), padL - 5, y + 3);
      }

      // X 轴时间标签（最多 6 个）
      const n = ts.length;
      ctx.textAlign = 'center';
      for (let i = 0; i < Math.min(6, n); i++) {
        const idx = Math.round(i * (n - 1) / Math.max(1, Math.min(6, n) - 1));
        const x = padL + iw * idx / Math.max(1, n - 1);
        ctx.fillText(ts[idx].slice(5, 16), x, H - 6);
      }

      // 折线
      series.forEach(s => {
        ctx.strokeStyle = s.color;
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        let started = false;
        s.data.forEach((v, i) => {
          if (v == null) return;
          const x = padL + iw * i / Math.max(1, n - 1);
          const y = padT + ih * (1 - v / max);
          if (!started) { ctx.moveTo(x, y); started = true; }
          else ctx.lineTo(x, y);
        });
        ctx.stroke();
      });
    }

    query();
    ensureWizard();
    refreshStatus();
    setInterval(refreshStatus, 30000);
  }

  // ---------------- 主题统计页 ----------------

  if (page === '/topics.html') { initTopics(); }

  function initTopics() {
    const pad = n => String(n).padStart(2, '0');
    const now = new Date();
    const fmt = dt => `${dt.getFullYear()}-${pad(dt.getMonth() + 1)}-${pad(dt.getDate())}T${pad(dt.getHours())}:${pad(dt.getMinutes())}`;
    let order = 'msg';
    let bucket = '5m';
    let topic = 'FMO/RAW';
    let viewState = null;   // 时间轴缩放/平移视图状态（自动刷新时保持用户正在看的窗口）

    // 默认近 7 天
    const s7 = new Date(now); s7.setDate(now.getDate() - 7);
    $('from').value = fmt(s7);
    $('to').value = fmt(now);

    // 读取主题配置状态
    (async () => {
      try {
        const d = await api('/api/topic-config');
        topic = d.topic;
        $('topic-name').textContent = d.topic + ' /#';
        const st = $('ingest-status');
        if (d.enabled) {
          st.className = d.pending ? 'status-err' : 'status-ok';
          st.textContent = d.pending
            ? `已启用但 ${d.pending} 待确认（配置页点「测试连接」）`
            : `已启用（已接收 ${fmtNum(d.total_ingested)} 条，最近 ${d.last_ingest_at || '-'}）`;
        } else {
          st.className = 'status-err';
          st.textContent = '未启用主题统计（配置页开启）';
        }
      } catch (e) { /* 401 */ }
    })();

    document.querySelectorAll('.filter-bar .chip[data-range]').forEach(b => {
      b.onclick = () => {
        document.querySelectorAll('.filter-bar .chip[data-range]').forEach(x => x.classList.remove('active'));
        b.classList.add('active');
        const r = b.dataset.range;
        if (r === 'today') { $('from').value = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T00:00`; $('to').value = fmt(now); }
        else if (r === 'yesterday') { const y = new Date(now); y.setDate(now.getDate() - 1); $('from').value = `${y.getFullYear()}-${pad(y.getMonth() + 1)}-${pad(y.getDate())}T00:00`; $('to').value = `${y.getFullYear()}-${pad(y.getMonth() + 1)}-${pad(y.getDate())}T23:59`; }
        else if (r === '30d') { const s = new Date(now); s.setDate(now.getDate() - 30); $('from').value = fmt(s); $('to').value = fmt(now); }
        else { const s = new Date(now); s.setDate(now.getDate() - 7); $('from').value = fmt(s); $('to').value = fmt(now); }
        query();
      };
    });
    document.querySelectorAll('.filter-bar .chip[data-order]').forEach(b => {
      b.onclick = () => {
        document.querySelectorAll('.filter-bar .chip[data-order]').forEach(x => x.classList.remove('active'));
        b.classList.add('active');
        order = b.dataset.order;
        query();
      };
    });
    document.querySelectorAll('.filter-bar .chip[data-bucket]').forEach(b => {
      b.onclick = () => {
        document.querySelectorAll('.filter-bar .chip[data-bucket]').forEach(x => x.classList.remove('active'));
        b.classList.add('active');
        bucket = b.dataset.bucket;
        loadTimeline();
      };
    });
    $('query').onclick = query;
    $('export').onclick = () => {
      const f = $('from').value, t = $('to').value;
      if (!f || !t) return;
      location.href = `/api/topic-export.csv?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}&order=${order}`;
    };

    async function query() {
      const f = $('from').value, t = $('to').value;
      if (!f || !t) { alert('请选择起止时间'); return; }
      $('query').disabled = true;
      try {
        const d = await api(`/api/topic-leaderboard?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}&order=${order}&limit=200`);
        if (!d.ok) { alert(d.error || '查询失败'); return; }
        await getBlacklistActive();
        $('range-desc').textContent = `${d.from.replace('T', ' ')} 至 ${d.to.replace('T', ' ')}`;
        $('total-rows').textContent = d.rows.length ? `共 ${d.rows.length} 个呼号` : '';
        render(d.rows, d.topic);
        loadTimeline();
      } catch (e) { /* 401 */ }
      finally { $('query').disabled = false; }
    }

    // ---- 时间轴：全员总量按时间桶（10s/1m/5m/1h）----
    // auto=true 为自动刷新：保持当前缩放/平移窗口，只更新数据
    async function loadTimeline(auto) {
      const f = $('from').value, t = $('to').value;
      if (!f || !t) return;
      try {
        const d = await api(`/api/topic-timeline?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}&bucket=${bucket}`);
        if (!d.ok) return;
        drawTimeline(d.rows, d.bucket, !!auto);
      } catch (e) { /* 401 */ }
    }

    function drawTimeline(rows, bkt, keepView) {
      const showSec = bkt === '10s';   // 10秒粒度时 X 轴显示到秒
      const cv = $('timeline-canvas');
      const tip = $('timeline-tooltip');
      const dpr = window.devicePixelRatio || 1;
      const W = cv.clientWidth, H = cv.clientHeight;
      cv.width = W * dpr; cv.height = H * dpr;
      const ctx = cv.getContext('2d');
      ctx.scale(dpr, dpr);
      const n = rows.length;

      const padL = 52, padR = 12, padT = 10, padB = 24;
      const iw = W - padL - padR, ih = H - padT - padB;

      // 视图状态：可见窗口（数据索引）；自动刷新时沿用上次窗口（新数据在末尾追加，旧索引不变）
      let vs = 0, ve = Math.max(0, n - 1);
      if (keepView && viewState && n > 0) {
        vs = Math.max(0, Math.min(viewState.vs, n - 1));
        ve = Math.max(vs, Math.min(viewState.ve, n - 1));
      }
      const st = {
        rows, n, ctx, tip, padL, padR, padT, padB, iw, ih, W, H, showSec,
        vs, ve,                       // 可见窗口 [vs, ve]
        MIN_WIN: 5,                   // 最小可见桶数（防止缩放到不可用）
        hover: -1,
      };

      const vn = () => st.ve - st.vs + 1;
      const xOf = i => st.padL + (i - st.vs) / Math.max(1, vn() - 1) * st.iw;
      const winMax = () => {
        let m = 0;
        for (let i = st.vs; i <= st.ve; i++) m = Math.max(m, st.rows[i].msgCount);
        return m <= 0 ? 1 : m * 1.1;
      };

      // 完整绘制（网格 + 线 + 面积 + hover）
      function drawAll() {
        const c = st.ctx;
        c.clearRect(0, 0, st.W, st.H);
        const max = winMax();

        // 网格 + Y 轴（按可见窗口自适应）
        c.strokeStyle = '#e5e5e5';
        c.fillStyle = '#999';
        c.font = '10px sans-serif';
        c.lineWidth = 1;
        for (let i = 0; i <= 4; i++) {
          const y = st.padT + st.ih * i / 4;
          c.beginPath(); c.moveTo(st.padL, y); c.lineTo(st.W - st.padR, y); c.stroke();
          const val = max * (1 - i / 4);
          c.textAlign = 'right';
          c.fillText(val >= 1000 ? (val / 1000).toFixed(1) + 'k' : Math.round(val).toString(), st.padL - 5, y + 3);
        }

        // X 轴标签（可见窗口内均匀 6 个，10s 粒度显示到秒）
        c.textAlign = 'center';
        const nv = vn();
        const labels = 6;
        for (let i = 0; i < labels; i++) {
          const idx = st.vs + Math.round(i * (nv - 1) / (labels - 1));
          const label = st.showSec ? st.rows[idx].ts.slice(11, 19) : st.rows[idx].ts.slice(5, 16);
          c.fillText(label, xOf(idx), st.H - 8);
        }

        if (st.n === 0) {
          c.fillStyle = '#999';
          c.fillText('该时间段无数据', st.W / 2 - 40, st.H / 2);
          st.tip.style.display = 'none';
          return;
        }

        // 折线 + 面积（可见窗口）
        c.strokeStyle = '#1565c0';
        c.lineWidth = 1.5;
        c.beginPath();
        for (let i = st.vs; i <= st.ve; i++) {
          const x = xOf(i), y = st.padT + st.ih * (1 - st.rows[i].msgCount / max);
          if (i === st.vs) c.moveTo(x, y); else c.lineTo(x, y);
        }
        c.stroke();
        c.lineTo(xOf(st.ve), st.padT + st.ih);
        c.lineTo(xOf(st.vs), st.padT + st.ih);
        c.closePath();
        c.fillStyle = 'rgba(21,101,192,0.08)';
        c.fill();

        // hover 高亮 + tooltip
        if (st.hover >= 0 && st.hover >= st.vs && st.hover <= st.ve) {
          const r = st.rows[st.hover];
          const x = xOf(st.hover), y = st.padT + st.ih * (1 - r.msgCount / max);
          c.fillStyle = '#c62828';
          c.beginPath(); c.arc(x, y, 4, 0, Math.PI * 2); c.fill();
          c.strokeStyle = 'rgba(198,40,40,0.4)';
          c.beginPath(); c.moveTo(x, st.padT); c.lineTo(x, st.padT + st.ih); c.stroke();
          st.tip.style.display = 'block';
          let rowsHtml = '';
          if (r.topUsers && r.topUsers.length) {
            rowsHtml = '<div style="border-top:1px solid #ccc;margin-top:6px;padding-top:6px;max-height:150px;overflow-y:auto">' +
              r.topUsers.map(u => `<div style="display:flex;justify-content:space-between;gap:16px"><span>${showName(u.name, u.uid)}</span><b>${fmtNum(u.msg)} 包</b></div>`).join('') +
              (r.userCount > r.topUsers.length ? `<div style="color:#999;margin-top:3px">… 共 ${r.userCount} 人发言</div>` : '') +
              '</div>';
          }
          st.tip.innerHTML = `<b>${r.ts}</b><br>发言 ${r.userCount} 人 · 消息 ${fmtNum(r.msgCount)} 条 · ${fmtBytes(r.bytes)}${rowsHtml}`;
          const tipW = st.tip.offsetWidth, tipH = st.tip.offsetHeight;
          let tx = x - tipW / 2, ty = y - tipH - 12;
          if (tx < 4) tx = 4;
          if (tx + tipW > st.W - 4) tx = st.W - tipW - 4;
          if (ty < 4) ty = y + 14;
          st.tip.style.left = tx + 'px';
          st.tip.style.top = ty + 'px';
        } else {
          st.tip.style.display = 'none';
        }
      }

      // ---- 交互 ----

      // 滚轮缩放（以鼠标位置为锚点；上滚放大，下滚缩小）
      cv.addEventListener('wheel', e => {
        e.preventDefault();
        if (st.n < 2) return;
        const rect = cv.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const nv = vn();
        const idx = st.vs + Math.max(0, Math.min(1, (mx - st.padL) / st.iw)) * (nv - 1);   // 锚点数据
        const zoom = e.deltaY < 0 ? 1.6 : 1 / 1.6;
        let nw = Math.max(st.MIN_WIN, Math.min(st.n, Math.round(nv / zoom)));
        if (nw === nv) return;
        const ratio = Math.max(0, Math.min(1, (mx - st.padL) / st.iw));
        let ns = Math.round(idx - ratio * (nw - 1));
        ns = Math.max(0, Math.min(st.n - nw, ns));
        st.vs = ns; st.ve = ns + nw - 1;
        viewState = { vs: st.vs, ve: st.ve };   // 同步视图状态（自动刷新时保持）
        st.hover = -1;
        drawAll();
      }, { passive: false });

      // 拖拽平移
      let dragging = false, dragX = 0, dragVs = 0;
      cv.onmousedown = e => {
        dragging = true;
        dragX = e.clientX; dragVs = st.vs;
      };
      cv.onmousemove = e => {
        const rect = cv.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        if (dragging) {
          const nv = vn();
          const shift = Math.round((mx - dragX) / st.iw * (nv - 1));
          if (shift !== 0) {
            let ns = dragVs - shift;
            ns = Math.max(0, Math.min(st.n - nv, ns));
            st.vs = ns; st.ve = ns + nv - 1;
            viewState = { vs: st.vs, ve: st.ve };   // 同步视图状态
            st.hover = -1;
            drawAll();
          }
        } else {
          // hover：可见窗口内最近点
          const nv = vn();
          const idx = st.vs + Math.max(0, Math.min(1, (mx - st.padL) / st.iw)) * (nv - 1);
          const h = Math.max(st.vs, Math.min(st.ve, Math.round(idx)));
          if (h !== st.hover) { st.hover = h; drawAll(); }
        }
      };
      cv.onmouseup = () => { dragging = false; };
      cv.onmouseleave = () => { dragging = false; st.hover = -1; drawAll(); };
      // 双击恢复全量视图
      cv.ondblclick = () => {
        st.vs = 0; st.ve = st.n - 1; st.hover = -1;
        viewState = null;   // 全量视图
        drawAll();
      };

      drawAll();
      // 记录当前视图（供自动刷新沿用；全量时不记录，保持跟随新数据）
      if (st.vs > 0 || st.ve < st.n - 1) viewState = { vs: st.vs, ve: st.ve };
    }

    function render(rows, tpc) {
      const tbody = $('rows');
      tbody.innerHTML = '';
      $('empty').classList.toggle('hidden', rows.length > 0);
      rows.forEach((r, i) => {
        const tr = document.createElement('tr');
        const rankCls = i === 0 ? 'rank-top1' : i === 1 ? 'rank-top2' : i === 2 ? 'rank-top3' : '';
        const banned = blMap && blMap[r.name];
        tr.innerHTML = `
          <td class="num"><span class="${rankCls}">${i + 1}</span></td>
          <td><a class="name-cell" data-name="${esc(r.name)}">${showName(r.name, r.uid)}${banned ? '<span class="ban-badge">已拉黑</span>' : ''}</a></td>
          <td class="num">${r.deviceCount}</td>
          <td class="num">${fmtNum(r.totalMsg)}</td>
          <td class="num">${fmtBytes(r.totalBytes)}</td>
          <td>${banCellHtml(r)}</td>`;
        tr.querySelector('.name-cell').onclick = () => toggleDetail(tr, r.name, tpc);
        tbody.appendChild(tr);
      });
    }

    let openRow = null;
    async function toggleDetail(tr, name, tpc) {
      if (openRow && openRow.parentNode === tr.nextSibling) { openRow.remove(); openRow = null; return; }
      if (openRow) openRow.remove();
      const f = $('from').value, t = $('to').value;
      const d = await api(`/api/topic-leaderboard/${encodeURIComponent(name)}?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}`);
      const detail = document.createElement('tr');
      detail.className = 'detail-row';
      detail.innerHTML = `<td colspan="6"><div class="detail-box">
        <h4>呼号 ${esc(name)} — clientid 明细（${d.rows.length} 行）</h4>
        <table class="detail-table">
          <thead><tr><th>clientid</th><th>主题</th><th class="num">消息数</th><th class="num">字节数</th></tr></thead>
          <tbody></tbody>
        </table></div></td>`;
      const tb = detail.querySelector('tbody');
      const byCid = {};
      d.rows.forEach(r => {
        if (!byCid[r.clientId]) byCid[r.clientId] = { cid: r.clientId, uid: r.uid, msg: 0, bytes: 0, topics: new Set() };
        const g = byCid[r.clientId];
        g.msg += r.msgCount; g.bytes += r.bytes; g.topics.add(r.topic);
      });
      Object.values(byCid).sort((a, b) => b.msg - a.msg).forEach(g => {
        const tr2 = document.createElement('tr');
        tr2.innerHTML = `<td class="mono">${showName(g.cid, g.uid)}</td><td class="mono">${esc([...g.topics].join(', '))}</td>
          <td class="num">${fmtNum(g.msg)}</td><td class="num">${fmtBytes(g.bytes)}</td>`;
        tb.appendChild(tr2);
      });
      tr.after(detail);
      openRow = detail;
    }

    query();
    ensureWizard();
    refreshStatus();
    setInterval(refreshStatus, 30000);
    refreshAfterBl = () => { query(); loadTimeline(); };
    bindBanActions($('rows'));
    // 时间轴自动刷新（30 秒；勾选"自动刷新"才刷，页面隐藏时不刷，保持缩放/平移窗口）
    setInterval(() => {
      if (document.hidden) return;
      if (!$('auto-refresh') || !$('auto-refresh').checked) return;
      loadTimeline(true);
      api('/api/topic-config').then(d => {
        if (d && d.enabled) {
          const st = $('ingest-status');
          st.textContent = `已启用（已接收 ${fmtNum(d.total_ingested)} 条，最近 ${d.last_ingest_at || '-'}）`;
        }
      }).catch(() => {});
    }, 30000);
  }

  // ---------------- 在线列表页 ----------------

  if (page === '/online.html') { initOnline(); }

  function initOnline() {
    refreshStatus();
    setInterval(refreshStatus, 30000);
    refreshAfterBl = load;

    // RFC3339 → 本地 "yyyy-MM-dd HH:mm:ss"（服务器时区）
    function fmtTs(rfc3339) {
      if (!rfc3339) return '-';
      const d = new Date(rfc3339);
      if (isNaN(d)) return rfc3339;
      const pad = n => String(n).padStart(2, '0');
      return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
    }
    // 在线时长
    function fmtDur(rfc3339) {
      if (!rfc3339) return '';
      const d = new Date(rfc3339);
      if (isNaN(d)) return '';
      const min = Math.floor((Date.now() - d.getTime()) / 60000);
      if (min < 1) return '刚连接';
      if (min < 60) return `${min} 分钟`;
      const h = Math.floor(min / 60), m = min % 60;
      return m ? `${h} 小时 ${m} 分` : `${h} 小时`;
    }

    async function load() {
      const d = await api('/api/online');
      const rows = d.rows || [];
      const sum = $('online-summary');
      const upd = $('online-updated');
      if (!d.collecting) {
        sum.className = 'status-err';
        sum.textContent = '未连接 EMQX';
        upd.textContent = '';
      } else {
        sum.className = '';
        sum.textContent = `在线 ${d.total} 个客户端`;
        upd.textContent = `（数据更新于 ${d.updated_at || '-'}，每 60 秒采集一次）`;
        upd.style.color = '#999';
        upd.style.fontSize = '12px';
      }
      const tbody = $('rows');
      tbody.innerHTML = '';
      $('empty').classList.toggle('hidden', rows.length > 0);
      rows.sort((a, b) => {
        if (a.is_anonymous !== b.is_anonymous) return a.is_anonymous ? 1 : -1;
        return String(a.name || a.clientid).localeCompare(String(b.name || b.clientid), 'zh');
      });
      rows.forEach(r => {
        const tr = document.createElement('tr');
        const banned = blMap && blMap[r.name];
        const name = r.name || r.clientid;   // 匿名显示 clientid
        tr.innerHTML = `
          <td>${esc(name)}${banned ? '<span class="ban-badge">已拉黑</span>' : ''}${r.uid ? `（${esc(r.uid)}）` : ''}</td>
          <td class="mono">${esc(r.clientid)}</td>
          <td class="mono">${esc(r.ip || '-')}</td>
          <td title="在线时长：${esc(fmtDur(r.connected_at))}">${fmtTs(r.connected_at)}</td>
          <td class="num">${fmtBytes(r.send_oct)}</td>
          <td class="num">${fmtBytes(r.recv_oct)}</td>
          <td>${banCellHtml({ name, isAnonymous: !!r.is_anonymous })}</td>`;
        tbody.appendChild(tr);
      });
    }

    load();
    ensureWizard();
    setInterval(() => {
      if (document.hidden) return;
      if (!$('auto-refresh') || !$('auto-refresh').checked) return;
      load();
    }, 30000);
    bindBanActions($('rows'));
  }

  // ---------------- 身份审计页 ----------------

  if (page === '/audit.html') { initAudit(); }

  function initAudit() {
    const pad = n => String(n).padStart(2, '0');
    const now = new Date();
    const fmt = dt => `${dt.getFullYear()}-${pad(dt.getMonth() + 1)}-${pad(dt.getDate())}T${pad(dt.getHours())}:${pad(dt.getMinutes())}`;
    let verdict = '';

    const s7 = new Date(now); s7.setDate(now.getDate() - 7);
    $('from').value = fmt(s7);
    $('to').value = fmt(now);

    // 身份控制开关状态
    (async () => {
      try {
        const d = await api('/api/identity-control');
        const el = $('ic-status');
        if (d.enabled) { el.className = 'status-ok'; el.textContent = '身份控制已启用（伪造即自动拉黑）'; }
        else { el.className = 'status-err'; el.textContent = '身份控制已关闭（仅记录提醒，不自动拉黑）'; }
      } catch (e) { /* 401 */ }
    })();

    document.querySelectorAll('.filter-bar .chip[data-range]').forEach(b => {
      b.onclick = () => {
        document.querySelectorAll('.filter-bar .chip[data-range]').forEach(x => x.classList.remove('active'));
        b.classList.add('active');
        const r = b.dataset.range;
        if (r === '1h') { const s = new Date(now); s.setHours(now.getHours() - 1); $('from').value = fmt(s); $('to').value = fmt(now); }
        else if (r === 'today') { $('from').value = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T00:00`; $('to').value = fmt(now); }
        else if (r === '30d') { const s = new Date(now); s.setDate(now.getDate() - 30); $('from').value = fmt(s); $('to').value = fmt(now); }
        else { const s = new Date(now); s.setDate(now.getDate() - 7); $('from').value = fmt(s); $('to').value = fmt(now); }
        query();
      };
    });
    document.querySelectorAll('.filter-bar .chip[data-verdict]').forEach(b => {
      b.onclick = () => {
        document.querySelectorAll('.filter-bar .chip[data-verdict]').forEach(x => x.classList.remove('active'));
        b.classList.add('active');
        verdict = b.dataset.verdict;
        query();
      };
    });
    $('query').onclick = query;

    const verdictCls = { KICK: '#c62828', WARN: '#e65100', FAIL: '#999' };
    const verdictTxt = { KICK: '身份不符', WARN: '未知身份', FAIL: '非法包' };

    async function query() {
      const f = $('from').value, t = $('to').value;
      if (!f || !t) { alert('请选择起止时间'); return; }
      $('query').disabled = true;
      const started = Date.now();
      try {
        const d = await api(`/api/audit-packets?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}${verdict ? `&verdict=${verdict}` : ''}&limit=300`);
        if (!d.ok) { alert(d.error || '查询失败'); return; }
        $('range-desc').textContent = `${d.from.replace('T', ' ')} 至 ${d.to.replace('T', ' ')}`;
        const c = d.counts || {};
        $('total-rows').textContent = `KICK ${c.KICK || 0} · WARN ${c.WARN || 0} · FAIL ${c.FAIL || 0}（显示 ${d.rows.length} 条）`;
        $('query-time').textContent = `查询耗时 ${Date.now() - started}ms`;
        render(d.rows);
      } catch (e) { /* 401 */ }
      finally { $('query').disabled = false; }
    }

    function render(rows) {
      const tbody = $('rows');
      tbody.innerHTML = '';
      $('empty').classList.toggle('hidden', rows.length > 0);
      rows.forEach(r => {
        const tr = document.createElement('tr');
        const disp = r.ban ? '已自动拉黑' : (r.verdict === 'KICK' ? '仅记录（身份控制关闭或拉黑失败）' : '-');
        tr.innerHTML = `
          <td><span style="color:${verdictCls[r.verdict] || '#333'};font-weight:600">${verdictTxt[r.verdict] || r.verdict}</span></td>
          <td>${esc(r.ts)}</td>
          <td>${r.connCallsign ? esc(r.connCallsign) + (r.connUid ? `（${esc(r.connUid)}）` : '') : '<span class="ban-note">匿名</span>'}</td>
          <td>${r.pktCallsign ? esc(r.pktCallsign) + (r.pktUid ? `（${esc(r.pktUid)}）` : '') : '<span class="ban-note">-</span>'}</td>
          <td class="mono">${esc(r.clientId)}</td>
          <td class="num">${r.len ?? '-'}</td>
          <td class="num">${r.frameNum ?? '-'}</td>
          <td class="num">${r.smeter ?? '-'}</td>
          <td class="num">${r.crcOk === null || r.crcOk === undefined ? '-' : (r.crcOk ? '<span style="color:#2e7d32">✓</span>' : '<span style="color:#c62828">✗</span>')}</td>
          <td>${r.ban ? '<span style="color:#c62828;font-weight:600">' + disp + '</span>' : '<span class="ban-note">' + esc(disp) + '</span>'}</td>`;
        tbody.appendChild(tr);
      });
    }

    query();
    ensureWizard();
    refreshStatus();
    setInterval(refreshStatus, 30000);
    setInterval(() => {
      if (document.hidden) return;
      if (!$('auto-refresh') || !$('auto-refresh').checked) return;
      query();
    }, 30000);
  }

  // ---------------- 黑名单页 ----------------

  if (page === '/blacklist.html') { initBlacklist(); }

  function initBlacklist() {
    refreshStatus();
    setInterval(refreshStatus, 30000);
    refreshAfterBl = load;

    async function load() {
      const d = await api('/api/blacklist/active');
      const st = $('bl-status');
      if (!d.emqx_reachable) {
        st.className = 'status-err';
        st.textContent = '未连接 EMQX（黑名单操作不可用，仅展示本地记录）';
      } else {
        st.className = '';
        st.textContent = `当前生效 ${d.local.length} 个${d.emqx_only.length ? `，EMQX 侧另有 ${d.emqx_only.length} 个手动拉黑` : ''}`;
      }
      // 生效列表：本地记录 + EMQX 侧对照（来源标记）
      const tbody = $('active-rows');
      tbody.innerHTML = '';
      const rows = d.local.map(x => Object.assign({}, x, { src: 'local' }))
        .concat(d.emqx_only.map(x => Object.assign({}, x, { src: 'emqx' })));
      $('active-empty').classList.toggle('hidden', rows.length > 0);
      rows.forEach(x => {
        const tr = document.createElement('tr');
        tr.innerHTML = `
          <td>${esc(x.who)}${x.src === 'emqx' ? ' <span class="ban-badge">EMQX</span>' : ''}</td>
          <td class="ban-reason">${esc(x.reason || '-')}</td>
          <td>${x.until ? esc(x.until) : '永久'}</td>
          <td>${esc(x.operator || (x.src === 'emqx' ? 'EMQX 手动' : '-'))}</td>
          <td>${esc(x.createdAt || '-')}</td>
          <td>${x.src === 'emqx' ? '<span class="ban-note">请到 EMQX 解封</span>' : `<button class="btn btn-small ban-btn banned" data-unban="${esc(x.who)}">解封</button>`}</td>`;
        tbody.appendChild(tr);
      });
      bindBanActions(tbody);   // 复用解封事件

      // 操作历史
      const h = await api('/api/blacklist/history');
      const htbody = $('history-rows');
      htbody.innerHTML = '';
      $('history-empty').classList.toggle('hidden', h.rows.length > 0);
      h.rows.forEach(x => {
        const isBan = x.action === 'ban';
        const tr = document.createElement('tr');
        tr.innerHTML = `
          <td style="color:${isBan ? '#c62828' : '#2e7d32'};font-weight:600">${isBan ? '拉黑' : '解封'}</td>
          <td>${esc(x.who)}</td>
          <td class="ban-reason">${esc(x.reason || '-')}</td>
          <td>${x.until ? esc(x.until) : (isBan ? '永久' : '-')}</td>
          <td>${esc(x.operator)}</td>
          <td>${esc(x.createdAt)}</td>`;
        htbody.appendChild(tr);
      });
    }

    $('bl-add').onclick = () => openBanModal('');

    load();
    ensureWizard();
  }

  // ---------------- 说明页 ----------------

  if (page === '/help.html') {
    ensureWizard();
    refreshStatus();
    setInterval(refreshStatus, 30000);
  }

  // ---------------- 配置页 ----------------

  if (page === '/settings.html') { initSettings(); }

  function initSettings() {
    refreshStatus();
    setInterval(refreshStatus, 30000);

    // ---- 身份控制开关 ----
    (async () => {
      try {
        const d = await api('/api/identity-control');
        const el = $('ic-status');
        if (d.enabled) { el.className = 'status-ok'; el.textContent = '已启用（伪造即自动拉黑）'; }
        else { el.className = 'status-err'; el.textContent = '已关闭（仅记录提醒）'; }
      } catch (e) { /* 401 */ }
    })();
    const setIc = async enabled => {
      const msg = $('ic-msg');
      msg.className = 'form-msg';
      msg.textContent = '保存中…';
      try {
        const d = await api('/api/identity-control', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ enabled })
        });
        if (d.ok) {
          msg.className = 'form-msg ok';
          msg.textContent = enabled ? '身份控制已启用' : '已关闭（仅记录提醒，不自动拉黑）';
          const el = $('ic-status');
          el.className = enabled ? 'status-ok' : 'status-err';
          el.textContent = enabled ? '已启用（伪造即自动拉黑）' : '已关闭（仅记录提醒）';
        } else { msg.className = 'form-msg err'; msg.textContent = d.error || '保存失败'; }
      } catch (e) { /* 401 */ }
    };
    $('ic-enable').onclick = () => setIc(true);
    $('ic-disable').onclick = () => setIc(false);

    // ---- 版本与更新（OTA）----
    const upModeTxt = { self: '裸机/服务部署（支持自更新）', docker: 'Docker 容器（不支持自更新）', manual: '手动部署' };
    async function upCheck() {
      const msg = $('up-msg');
      msg.className = 'form-msg';
      msg.textContent = '检查中…';
      try {
        const d = await api('/api/update/check');
        $('up-current').textContent = d.current;
        const lr = $('up-latest-row'), mr = $('up-mode-row'), dh = $('up-docker-hint');
        lr.style.display = '';
        lr.innerHTML = `最新版本：<b>${d.latest || '-'}</b>`;
        mr.style.display = '';
        mr.innerHTML = `部署模式：<b>${upModeTxt[d.update_mode] || d.update_mode}</b>`;
        if (d.docker_hint) { dh.style.display = ''; dh.textContent = '⚠️ ' + d.docker_hint; }
        else dh.style.display = 'none';
        if (d.error) { msg.className = 'form-msg err'; msg.textContent = d.error; }
        else if (d.has_update && d.update_mode !== 'docker') {
          $('up-apply').style.display = '';
          msg.className = 'form-msg ok';
          msg.textContent = `发现新版本 v${d.latest}，可立即更新`;
        } else {
          $('up-apply').style.display = 'none';
          msg.className = 'form-msg';
          msg.textContent = '已是最新版本';
        }
      } catch (e) { /* 401 */ }
    }
    $('up-check').onclick = upCheck;
    $('up-apply').onclick = async () => {
      if (!confirm('确认更新到最新版本？更新期间服务将自动重启（约 10 秒），页面会短暂中断。')) return;
      const msg = $('up-msg'), prog = $('up-progress');
      msg.className = 'form-msg';
      msg.textContent = '正在下载并校验…';
      prog.textContent = '（大版本下载可能需要 1-2 分钟，请勿关闭页面）';
      $('up-apply').disabled = true;
      try {
        const d = await api('/api/update/apply', { method: 'POST' });
        if (d.ok) {
          msg.className = 'form-msg ok';
          msg.textContent = d.message || '更新中，服务将自动重启';
          prog.textContent = '服务重启后（约 10 秒）请刷新页面查看新版本号';
          setTimeout(() => { location.reload(); }, 12000);
        } else {
          msg.className = 'form-msg err';
          msg.textContent = d.error || '更新失败';
          $('up-apply').disabled = false;
        }
      } catch (e) {
        // 更新成功时进程退出可能导致连接中断——静默等待自动刷新
        prog.textContent = '服务正在重启，请稍后刷新页面…';
        setTimeout(() => { location.reload(); }, 12000);
      }
    };
    upCheck();

    (async () => {
      try {
        const d = await api('/api/config');
        $('emqx-url').value = d.emqx_url || '';
        $('run-port').textContent = d.listen_port;
        $('run-retention').textContent = d.data_retention_days + ' 天';
        $('run-status').textContent = d.status || (d.last_collect_ok ? '采集中' : '未采集');
        $('run-clients').textContent = d.online_clients ?? 0;
        if (d.configured) $('config-info').textContent = '已连接 EMQX（API Secret 不显示，如需修改请重新填写）';
      } catch (e) { /* 401 */ }
    })();

    $('save-config').onclick = async () => {
      const url = $('emqx-url').value.trim(), key = $('api-key').value.trim(), secret = $('api-secret').value.trim();
      const msg = $('config-msg');
      msg.className = 'form-msg err';
      if (!url || !key || !secret) { msg.textContent = '请填写地址、API Key、API Secret'; return; }
      msg.textContent = '正在测试连接…';
      $('save-config').disabled = true;
      try {
        const d = await api('/api/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ emqxUrl: url, apiKey: key, apiSecret: secret }) });
        if (d.ok) { msg.className = 'form-msg ok'; msg.textContent = '连接成功，开始采集（1 分钟后出数据）'; refreshStatus(); }
        else msg.textContent = d.error || '连接失败';
      } finally { $('save-config').disabled = false; }
    };

    $('disconnect').onclick = async () => {
      const d = await api('/api/config/disconnect', { method: 'POST' });
      if (d.ok) { $('config-msg').className = 'form-msg ok'; $('config-msg').textContent = '已断开监控'; refreshStatus(); }
    };

    $('change-pw').onclick = async () => {
      const oldPw = $('old-pw').value, newPw = $('new-pw').value;
      const msg = $('pw-msg');
      msg.className = 'form-msg err';
      if (!oldPw || newPw.length < 8) { msg.textContent = '请填写旧密码，新密码至少 8 个字符'; return; }
      const d = await api('/api/change-password', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ oldPassword: oldPw, newPassword: newPw }) });
      if (d.ok) { msg.className = 'form-msg ok'; msg.textContent = '密码已修改'; $('old-pw').value = ''; $('new-pw').value = ''; }
      else msg.textContent = d.error || '修改失败';
    };

    // ---- 主题统计 ----
    const showTopicReport = d => {
      const row = $('topic-pending-row');
      let html = '';
      if (d.pending) {
        html += `⚠️ 以下步骤响应超时（集群同步慢，资源可能已创建成功）：<b>${esc(d.pending)}</b><br>`;
      }
      if (d.failed) {
        html += `❌ 以下步骤配置失败（主题统计仍已启用，数据照常接收）：<b>${esc(d.failed)}</b><br>`;
      }
      if (html) {
        row.style.display = '';
        row.innerHTML = html + `请点击「测试连接」验证实际状态，或到 <b>EMQX Dashboard → 集成 → 连接器/规则</b> 查看。`;
      } else {
        row.style.display = 'none';
        row.innerHTML = '';
      }
    };
    // 配置报告：每步实际状态（EMQX 侧查询为准）
    const renderStatusReport = (s, container) => {
      const conn = s.connector, mid = s.middleware, rule = s.rule;
      const line = (ok, txt) => `<div>${ok ? '<span style="color:#2e7d32">✅</span>' : '<span style="color:#c62828">❌</span>'} ${txt}</div>`;
      let html = '';
      html += line(conn.exists, `连接器 emqx-monitor-ingest：${conn.exists ? `存在（状态 ${conn.state || '未知'}${conn.reason ? '，原因: ' + esc(conn.reason) : ''}）` : '不存在'}`);
      html += line(mid.exists, `${s.v6 ? '动作' : '桥接'} ${s.v6 ? 'emqx-monitor-ingest-action' : 'emqx-monitor-bridge'}：${mid.exists ? '存在' : '不存在'}`);
      html += line(rule.exists && rule.enabled, `规则 emqx-monitor-topic-rule：${rule.exists ? (rule.enabled ? '存在且已启用' : '存在但未启用') : '不存在'}`);
      html += `<div style="margin-top:6px;color:${s.ok ? '#2e7d32' : '#e65100'};font-weight:600">${s.ok ? '✅ 链路正常' : '❌ 链路不完整'}</div>`;
      container.innerHTML = html;
    };
    (async () => {
      try {
        const d = await api('/api/topic-config');
        $('topic-name').value = d.topic;
        $('topic-webhook-url').value = d.webhook_url || d.ingest_url;
        $('topic-webhook').textContent = d.webhook_url || d.ingest_url;
        showTopicReport(d);
        // 本机 IP 快速选择按钮（Linux 多网卡多 IP 场景）
        const pick = $('ip-quick-pick');
        if (pick && d.local_ips && d.local_ips.length) {
          const port = new URL(d.ingest_url || location.origin).port || '9527';
          d.local_ips.forEach(ip => {
            const b = document.createElement('button');
            b.className = 'chip';
            b.textContent = ip + ':' + port;
            b.title = '填入 http://' + ip + ':' + port + '/api/ingest';
            b.onclick = () => {
              $('topic-webhook-url').value = `http://${ip}:${port}/api/ingest`;
              const hint = $('topic-msg');
              hint.className = 'form-msg';
              hint.textContent = `已填入 ${ip}:${port}（EMQX 节点需能访问该地址）`;
            };
            pick.appendChild(b);
          });
        }
        if (d.enabled) {
          $('topic-status').textContent = '已启用' + (d.pending ? '（部分步骤待确认）' : '');
          $('topic-status').style.color = d.pending ? '#e65100' : '#2e7d32';
          $('topic-total').textContent = fmtNum(d.total_ingested);
          $('topic-last').textContent = d.last_ingest_at || '-';
        } else {
          $('topic-status').textContent = '未启用';
          $('topic-status').style.color = '#999';
        }
      } catch (e) { /* 401 */ }
    })();

    $('topic-enable').onclick = async () => {
      const topic = $('topic-name').value.trim() || 'FMO/RAW';
      const webhookUrl = $('topic-webhook-url').value.trim();
      const msg = $('topic-msg');
      msg.className = 'form-msg err';
      // 智能网段提示：webhook 是内网 IP 而 EMQX 是公网地址 → 异地节点可能无法上报
      try {
        const cfg = await api('/api/config');
        const emqxHost = (cfg.emqx_url || '').replace(/^https?:\/\//, '').split(/[/:]/)[0];
        const whHost = (webhookUrl || '').replace(/^https?:\/\//, '').split('/')[0].split(':')[0];
        const isPriv = h => {
          const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(h || '');
          if (!m) return null;
          const a = +m[1], b = +m[2];
          return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || a === 127;
        };
        if (whHost && emqxHost && isPriv(whHost) === true && isPriv(emqxHost) !== true) {
          msg.textContent = `⚠️ Webhook 是内网地址（${whHost}），而 EMQX 地址是公网（${emqxHost}）——异地节点可能无法上报。请确认 Webhook 地址对所有 EMQX 节点可见，或改用上方公网地址。`;
          return;
        }
      } catch (e) { /* 配置未就绪时跳过提示 */ }
      msg.textContent = '正在配置 EMQX 规则引擎…';
      $('topic-enable').disabled = true;
      try {
        const d = await api('/api/topic-config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ enable: true, topic, webhookUrl }) });
        if (d.ok) {
          // 立即启用（不阻塞）；配置报告展示异常
          const hasIssue = !!(d.pending || d.failed || (d.status && !d.status.Ok));
          msg.className = hasIssue ? 'form-msg err' : 'form-msg ok';
          $('topic-status').textContent = hasIssue ? '已启用（配置有异常）' : '已启用';
          $('topic-status').style.color = hasIssue ? '#e65100' : '#2e7d32';
          $('topic-webhook').textContent = d.webhook_url;
          $('topic-webhook-url').value = d.webhook_url;
          showTopicReport(d);
          // 实际状态报告（EMQX 侧查询为准）
          if (d.status) {
            const res = $('topic-test-result');
            res.style.display = '';
            renderStatusReport(d.status, res);
          }
          if (hasIssue) {
            msg.textContent = d.hint || '主题统计已启用（数据照常接收），但配置存在异常——详见下方报告，修复后可重新启用或点「测试连接」。';
          } else {
            msg.textContent = `已启用，统计主题 ${d.topic} /#（1 分钟后出数据）`;
          }
        } else msg.textContent = d.error || '启用失败';
      } finally { $('topic-enable').disabled = false; }
    };

    $('topic-disable').onclick = async () => {
      const msg = $('topic-msg');
      msg.className = 'form-msg err';
      const d = await api('/api/topic-config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ enable: false }) });
      if (d.ok) {
        msg.className = 'form-msg ok';
        msg.textContent = '已停用，规则引擎已从 EMQX 移除';
        $('topic-status').textContent = '未启用';
        $('topic-status').style.color = '#999';
        showTopicReport({});
      } else msg.textContent = d.error || '停用失败';
    };

    // ---- 测试连接：验证 EMQX 侧四件套真实状态（集群超时后确认用）----
    $('topic-test').onclick = async () => {
      const btn = $('topic-test');
      const res = $('topic-test-result');
      btn.disabled = true;
      res.className = 'form-msg';
      res.textContent = '正在测试…';
      try {
        const d = await api('/api/topic-test');
        if (!d.ok) { res.className = 'form-msg err'; res.textContent = d.error || '测试失败'; return; }
        const s = d.status;
        const conn = s.connector;
        const mid = s.middleware;
        const rule = s.rule;
        const line = (ok, txt) => `<div>${ok ? '<span style="color:#2e7d32">✅</span>' : '<span style="color:#c62828">❌</span>'} ${txt}</div>`;
        let html = '';
        html += line(conn.exists, `连接器 emqx-monitor-ingest：${conn.exists ? `存在（状态 ${conn.state || '未知'}${conn.reason ? '，原因: ' + esc(conn.reason) : ''}）` : '不存在'}`);
        html += line(mid.exists, `${s.v6 ? '动作' : '桥接'} ${s.v6 ? 'emqx-monitor-ingest-action' : 'emqx-monitor-bridge'}：${mid.exists ? '存在' : '不存在'}`);
        html += line(rule.exists && rule.enabled, `规则 emqx-monitor-topic-rule：${rule.exists ? (rule.enabled ? '存在且已启用' : '存在但未启用') : '不存在'}`);
        html += `<div style="margin-top:6px;color:${s.ok ? '#2e7d32' : '#e65100'};font-weight:600">${s.ok ? '✅ 链路正常，主题统计工作正常' : '❌ 链路不完整'}</div>`;
        if (!s.ok) html += `<div style="color:#999;margin-top:4px">请到 EMQX Dashboard → 集成 → 连接器/规则 查看真实状态，或重新启用主题统计。${d.dashboard_hint || ''}</div>`;
        res.innerHTML = html;
        res.style.display = '';
        // 测试通过后刷新状态显示（清除待确认）
        if (s.ok) {
          const c = await api('/api/topic-config');
          showTopicReport(c);
          if (c.enabled) { $('topic-status').textContent = '已启用'; $('topic-status').style.color = '#2e7d32'; }
        }
      } catch (e) { /* 401 */ }
      finally { btn.disabled = false; }
    };

    // ---- 首次引导横幅 ----
    (async () => {
      try {
        const d = await api('/api/status');
        if (!d.wizard_done && !d.configured) {
          $('wizard-banner').classList.remove('hidden');
        }
      } catch (e) { /* 401 */ }
    })();

    // ---- 兼容性自检 ----
    $('check-run').onclick = async () => {
      const msg = $('check-msg'), box = $('check-result');
      msg.className = 'form-msg err';
      msg.textContent = '正在检测…';
      $('check-run').disabled = true;
      try {
        const d = await api('/api/check');
        if (!d.ok) { msg.textContent = d.error || '检测失败'; box.innerHTML = ''; return; }
        msg.className = 'form-msg ok';
        msg.textContent = `检测完成：EMQX ${d.version}`;
        const rows = d.checks.map(c =>
          `<div style="display:flex;justify-content:space-between;gap:12px"><span>${c.ok ? '✓' : '✗'} ${esc(c.name)} <span style="color:#999">${c.path}</span></span><span style="color:${c.ok ? '#2e7d32' : '#c62828'}">${esc(c.note)}</span></div>`).join('');
        const warn = d.supported ? '' : `<div style="color:#c62828;font-weight:600;margin-top:8px">⚠ ${esc(d.suggested_upgrade || '')}</div>`;
        box.innerHTML = rows + warn;
      } catch (e) { msg.textContent = '检测失败'; }
      finally { $('check-run').disabled = false; }
    };

    // ---- 数据管理 ----
    async function loadStats() {
      try {
        const d = await api('/api/admin/stats');
        $('stat-minutes').textContent = fmtNum(d.minute_stats);
        $('stat-topics').textContent = fmtNum(d.topic_stats);
        $('stat-health').textContent = fmtNum(d.health_snapshots);
      } catch (e) { /* 401 */ }
    }
    loadStats();

    $('clear-data').onclick = async () => {
      const msg = $('clear-msg');
      msg.className = 'form-msg err';
      if (!confirm('确定清空全部统计数据？此操作不可恢复（保留管理员账号和 EMQX 配置）。')) return;
      if (!confirm('再次确认：清空后 30 天内的历史数据将全部丢失。')) return;
      msg.textContent = '正在清空…';
      try {
        const d = await api('/api/admin/clear-data', { method: 'POST' });
        if (d.ok) {
          msg.className = 'form-msg ok';
          msg.textContent = '已清空（呼号增量 ' + d.cleared.minute_stats + ' / 主题 ' + d.cleared.topic_stats + ' / 健康 ' + d.cleared.health_snapshots + ' 行），从当前时刻重新统计';
          loadStats();
        } else msg.textContent = d.error || '清空失败';
      } catch (e) { msg.textContent = '清空失败'; }
    };

    // ---- 完全重置 ----
    $('reset-tool').onclick = async () => {
      const msg = $('reset-msg');
      msg.className = 'form-msg err';
      if (!confirm('确定重置审计监控工具？将删除管理员账号、EMQX 配置与全部数据，恢复到首次安装状态。')) return;
      if (!confirm('再次确认：重置后必须重新设置管理员账号并重新连接 EMQX，且 EMQX 上的规则引擎会被移除。')) return;
      if (!confirm('最后一次确认：所有历史数据（30 天）将永久丢失。')) return;
      msg.textContent = '正在重置…';
      try {
        const d = await api('/api/admin/reset', { method: 'POST' });
        if (d.ok) {
          msg.textContent = '重置完成，正在跳转首次设置…';
          setTimeout(() => { location.href = '/setup.html'; }, 800);
        } else msg.textContent = d.error || '重置失败';
      } catch (e) { msg.textContent = '重置失败'; }
    };
  }
})();
