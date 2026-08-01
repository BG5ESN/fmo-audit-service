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

  $('logout').addEventListener('click', async () => {
    await fetch('/api/logout', { method: 'POST' });
    location.href = '/login.html';
  });

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
        tr.innerHTML = `
          <td class="num"><span class="${rankCls}">${i + 1}</span></td>
          <td><a class="name-cell" data-name="${esc(r.name)}">${esc(r.name)}</a></td>
          <td class="num">${r.deviceCount}</td>
          <td class="num">${fmtBytes(r.totalOct)}</td>
          <td class="num">${fmtNum(r.totalMsg)}</td>
          <td class="num">${fmtNum(r.totalPkt)}</td>
          <td class="num">${r.reconnectCount > 0 ? '<span class="reconnect-badge">' + r.reconnectCount + '</span>' : 0}</td>`;
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
      detail.innerHTML = `<td colspan="7"><div class="detail-box">
        <h4>呼号 ${esc(name)} — clientid 明细（${d.rows.length} 行）</h4>
        <table class="detail-table">
          <thead><tr><th>clientid</th><th class="num">发送字节</th><th class="num">接收字节</th><th class="num">发送消息</th><th class="num">接收消息</th><th class="num">发送包</th><th class="num">接收包</th><th>重连</th></tr></thead>
          <tbody></tbody>
        </table></div></td>`;
      const tb = detail.querySelector('tbody');
      // 按 clientid 聚合
      const byCid = {};
      d.rows.forEach(r => {
        if (!byCid[r.clientId]) byCid[r.clientId] = { cid: r.clientId, so: 0, ro: 0, sm: 0, rm: 0, sp: 0, rp: 0, rc: 0, ip: r.ipAddress };
        const g = byCid[r.clientId];
        g.so += r.sendOct; g.ro += r.recvOct; g.sm += r.sendMsg; g.rm += r.recvMsg; g.sp += r.sendPkt; g.rp += r.recvPkt; g.rc += r.reconnect ? 1 : 0;
      });
      Object.values(byCid).sort((a, b) => (b.so + b.ro) - (a.so + a.ro)).forEach(g => {
        const tr2 = document.createElement('tr');
        tr2.innerHTML = `<td class="mono">${esc(g.cid)}</td><td class="num">${fmtBytes(g.so)}</td><td class="num">${fmtBytes(g.ro)}</td>
          <td class="num">${fmtNum(g.sm)}</td><td class="num">${fmtNum(g.rm)}</td><td class="num">${fmtNum(g.sp)}</td><td class="num">${fmtNum(g.rp)}</td>
          <td>${g.rc > 0 ? '<span class="reconnect-badge">' + g.rc + '</span>' : ''}</td>`;
        tb.appendChild(tr2);
      });
      tr.after(detail);
      openRow = detail;
    }

    query();
    refreshStatus();
    setInterval(refreshStatus, 30000);
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
          st.className = 'status-ok';
          st.textContent = `已启用（已接收 ${fmtNum(d.total_ingested)} 条，最近 ${d.last_ingest_at || '-'}）`;
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
        $('range-desc').textContent = `${d.from.replace('T', ' ')} 至 ${d.to.replace('T', ' ')}`;
        $('total-rows').textContent = d.rows.length ? `共 ${d.rows.length} 个呼号` : '';
        render(d.rows, d.topic);
        loadTimeline();
      } catch (e) { /* 401 */ }
      finally { $('query').disabled = false; }
    }

    // ---- 时间轴：全员总量按时间桶（1m/5m/1h）----
    async function loadTimeline() {
      const f = $('from').value, t = $('to').value;
      if (!f || !t) return;
      try {
        const d = await api(`/api/topic-timeline?from=${encodeURIComponent(f)}&to=${encodeURIComponent(t)}&bucket=${bucket}`);
        if (!d.ok) return;
        drawTimeline(d.rows);
      } catch (e) { /* 401 */ }
    }

    function drawTimeline(rows) {
      const cv = $('timeline-canvas');
      const tip = $('timeline-tooltip');
      const dpr = window.devicePixelRatio || 1;
      const W = cv.clientWidth, H = cv.clientHeight;
      cv.width = W * dpr; cv.height = H * dpr;
      const ctx = cv.getContext('2d');
      ctx.scale(dpr, dpr);

      const padL = 52, padR = 12, padT = 10, padB = 24;
      const iw = W - padL - padR, ih = H - padT - padB;
      const n = rows.length;
      let max = n ? Math.max(...rows.map(r => r.msgCount)) : 0;
      if (max <= 0) max = 1;
      max = max * 1.1;
      const xOf = i => padL + iw * i / Math.max(1, n - 1);
      const yOf = v => padT + ih * (1 - v / max);

      const state = { rows, ctx, W, H, padL, padR, padT, padB, iw, ih, n, max, xOf, yOf, tip };

      // 静态绘制（网格 + 线 + 面积）
      const drawStatic = s => {
        const c = s.ctx;
        c.clearRect(0, 0, s.W, s.H);
        c.strokeStyle = '#e5e5e5';
        c.fillStyle = '#999';
        c.font = '10px sans-serif';
        c.lineWidth = 1;
        for (let i = 0; i <= 4; i++) {
          const y = s.padT + s.ih * i / 4;
          c.beginPath(); c.moveTo(s.padL, y); c.lineTo(s.W - s.padR, y); c.stroke();
          const val = s.max * (1 - i / 4);
          c.textAlign = 'right';
          c.fillText(val >= 1000 ? (val / 1000).toFixed(1) + 'k' : Math.round(val).toString(), s.padL - 5, y + 3);
        }
        c.textAlign = 'center';
        for (let i = 0; i < Math.min(6, s.n); i++) {
          const idx = Math.round(i * (s.n - 1) / Math.max(1, Math.min(6, s.n) - 1));
          c.fillText(s.rows[idx].ts.slice(5, 16), s.xOf(idx), s.H - 8);
        }
        if (s.n === 0) {
          c.fillStyle = '#999';
          c.fillText('该时间段无数据', s.W / 2 - 40, s.H / 2);
          return;
        }
        c.strokeStyle = '#1565c0';
        c.lineWidth = 1.5;
        c.beginPath();
        for (let i = 0; i < s.n; i++) {
          const x = s.xOf(i), y = s.yOf(s.rows[i].msgCount);
          if (i === 0) c.moveTo(x, y); else c.lineTo(x, y);
        }
        c.stroke();
        c.lineTo(s.xOf(s.n - 1), s.padT + s.ih);
        c.lineTo(s.xOf(0), s.padT + s.ih);
        c.closePath();
        c.fillStyle = 'rgba(21,101,192,0.08)';
        c.fill();
      };

      // hover 叠加（只画高亮 + tooltip）
      const drawHover = (s, idx) => {
        drawStatic(s);
        if (idx < 0) { s.tip.style.display = 'none'; return; }
        const r = s.rows[idx];
        const x = s.xOf(idx), y = s.yOf(r.msgCount);
        const c = s.ctx;
        c.fillStyle = '#c62828';
        c.beginPath(); c.arc(x, y, 4, 0, Math.PI * 2); c.fill();
        c.strokeStyle = 'rgba(198,40,40,0.4)';
        c.beginPath(); c.moveTo(x, s.padT); c.lineTo(x, s.padT + s.ih); c.stroke();
        s.tip.style.display = 'block';
        let rowsHtml = '';
        if (r.topUsers && r.topUsers.length) {
          rowsHtml = '<div style="border-top:1px solid #ccc;margin-top:6px;padding-top:6px;max-height:150px;overflow-y:auto">' +
            r.topUsers.map(u => `<div style="display:flex;justify-content:space-between;gap:16px"><span>${esc(u.name)}</span><b>${fmtNum(u.msg)} 包</b></div>`).join('') +
            (r.userCount > r.topUsers.length ? `<div style="color:#999;margin-top:3px">… 共 ${r.userCount} 人发言</div>` : '') +
            '</div>';
        }
        s.tip.innerHTML = `<b>${r.ts}</b><br>发言 ${r.userCount} 人 · 消息 ${fmtNum(r.msgCount)} 条 · ${fmtBytes(r.bytes)}${rowsHtml}`;
        const tipW = s.tip.offsetWidth, tipH = s.tip.offsetHeight;
        let tx = x - tipW / 2, ty = y - tipH - 12;
        if (tx < 4) tx = 4;
        if (tx + tipW > s.W - 4) tx = s.W - tipW - 4;
        if (ty < 4) ty = y + 14;
        s.tip.style.left = tx + 'px';
        s.tip.style.top = ty + 'px';
      };

      drawStatic(state);

      // 绑定 hover（每次重绘只更新 state）
      cv.onmousemove = e => {
        const rect = cv.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const idx = state.n < 2 ? 0 : Math.max(0, Math.min(state.n - 1, Math.round((mx - state.padL) / state.iw * (state.n - 1))));
        drawHover(state, idx);
      };
      cv.onmouseleave = () => drawHover(state, -1);
    }

    function render(rows, tpc) {
      const tbody = $('rows');
      tbody.innerHTML = '';
      $('empty').classList.toggle('hidden', rows.length > 0);
      rows.forEach((r, i) => {
        const tr = document.createElement('tr');
        const rankCls = i === 0 ? 'rank-top1' : i === 1 ? 'rank-top2' : i === 2 ? 'rank-top3' : '';
        tr.innerHTML = `
          <td class="num"><span class="${rankCls}">${i + 1}</span></td>
          <td><a class="name-cell" data-name="${esc(r.name)}">${esc(r.name)}</a></td>
          <td class="num">${r.deviceCount}</td>
          <td class="num">${fmtNum(r.totalMsg)}</td>
          <td class="num">${fmtBytes(r.totalBytes)}</td>`;
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
      detail.innerHTML = `<td colspan="5"><div class="detail-box">
        <h4>呼号 ${esc(name)} — clientid 明细（${d.rows.length} 行）</h4>
        <table class="detail-table">
          <thead><tr><th>clientid</th><th>主题</th><th class="num">消息数</th><th class="num">字节数</th></tr></thead>
          <tbody></tbody>
        </table></div></td>`;
      const tb = detail.querySelector('tbody');
      const byCid = {};
      d.rows.forEach(r => {
        if (!byCid[r.clientId]) byCid[r.clientId] = { cid: r.clientId, msg: 0, bytes: 0, topics: new Set() };
        const g = byCid[r.clientId];
        g.msg += r.msgCount; g.bytes += r.bytes; g.topics.add(r.topic);
      });
      Object.values(byCid).sort((a, b) => b.msg - a.msg).forEach(g => {
        const tr2 = document.createElement('tr');
        tr2.innerHTML = `<td class="mono">${esc(g.cid)}</td><td class="mono">${esc([...g.topics].join(', '))}</td>
          <td class="num">${fmtNum(g.msg)}</td><td class="num">${fmtBytes(g.bytes)}</td>`;
        tb.appendChild(tr2);
      });
      tr.after(detail);
      openRow = detail;
    }

    query();
    refreshStatus();
    setInterval(refreshStatus, 30000);
  }

  // ---------------- 配置页 ----------------

  if (page === '/settings.html') { initSettings(); }

  function initSettings() {
    refreshStatus();
    setInterval(refreshStatus, 30000);

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
    (async () => {
      try {
        const d = await api('/api/topic-config');
        $('topic-name').value = d.topic;
        $('topic-webhook-url').value = d.webhook_url || d.ingest_url;
        $('topic-webhook').textContent = d.webhook_url || d.ingest_url;
        if (d.enabled) {
          $('topic-status').textContent = '已启用';
          $('topic-status').style.color = '#2e7d32';
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
      msg.textContent = '正在配置 EMQX 规则引擎…';
      $('topic-enable').disabled = true;
      try {
        const d = await api('/api/topic-config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ enable: true, topic, webhookUrl }) });
        if (d.ok) {
          msg.className = 'form-msg ok';
          msg.textContent = `已启用，统计主题 ${d.topic} /#（1 分钟后出数据）`;
          $('topic-status').textContent = '已启用';
          $('topic-status').style.color = '#2e7d32';
          $('topic-webhook').textContent = d.webhook_url;
          $('topic-webhook-url').value = d.webhook_url;
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
      } else msg.textContent = d.error || '停用失败';
    };
  }
})();
