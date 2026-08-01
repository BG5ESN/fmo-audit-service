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

  if (page === '/' ) { initLeaderboard(); }

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
          <td class="num">${r.device_count}</td>
          <td class="num">${fmtBytes(r.total_oct)}</td>
          <td class="num">${fmtNum(r.total_msg)}</td>
          <td class="num">${fmtNum(r.total_pkt)}</td>
          <td class="num">${r.reconnect_count > 0 ? '<span class="reconnect-badge">' + r.reconnect_count + '</span>' : 0}</td>`;
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
        if (!byCid[r.clientid]) byCid[r.clientid] = { cid: r.clientid, so: 0, ro: 0, sm: 0, rm: 0, sp: 0, rp: 0, rc: 0, ip: r.ip_address };
        const g = byCid[r.clientid];
        g.so += r.send_oct; g.ro += r.recv_oct; g.sm += r.send_msg; g.rm += r.recv_msg; g.sp += r.send_pkt; g.rp += r.recv_pkt; g.rc += r.reconnect ? 1 : 0;
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

    function esc(s) {
      return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    query();
    refreshStatus();
    setInterval(refreshStatus, 30000);
  }

  // ---------------- 健康页 ----------------

  if (page === '/health') { initHealth(); }

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
        { name: 'CPU %', color: '#d32f2f', data: rows.map(r => r.host_cpu_pct) },
        { name: '内存 %', color: '#1565c0', data: rows.map(r => r.host_mem_used_pct) },
        { name: '磁盘 %', color: '#2e7d32', data: rows.map(r => r.host_disk_used_pct) },
      ], '%');
      // 网络
      drawLine('host-net', 'legend-net', ts, [
        { name: '下行 KB/s', color: '#1565c0', data: rows.map(r => r.host_net_recv_kbps) },
        { name: '上行 KB/s', color: '#e65100', data: rows.map(r => r.host_net_send_kbps) },
      ], 'KB/s');
      // EMQX 连接数/消息速率
      drawLine('emqx-basic', 'legend-emqx', ts, [
        { name: '连接数', color: '#2e7d32', data: rows.map(r => r.emqx_connections) },
        { name: '消息速率 条/s', color: '#6a1b9a', data: rows.map(r => r.emqx_msg_rate) },
        { name: '节点负载 load1', color: '#e65100', data: rows.map(r => r.emqx_cpu_pct) },
      ], '');
      // 告警
      const alarms = rows.map(r => r.emqx_alarms).filter(Boolean);
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

  // ---------------- 配置页 ----------------

  if (page === '/settings') { initSettings(); }

  function initSettings() {
    refreshStatus();
    setInterval(refreshStatus, 30000);

    (async () => {
      try {
        const d = await api('/api/config');
        $('emqx-url').value = d.emqx_url || '';
        $('run-port').textContent = d.listen_port;
        $('run-retention').textContent = d.data_retention_days + ' 天';
        $('run-status').textContent = d.status || '未采集';
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
  }
})();
