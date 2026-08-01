// EMQX 监控面板 — 前端逻辑
(function () {
  'use strict';

  var POLL_MS = 5000;           // 与后端轮询节奏一致
  var currentDetailUsername = null;

  var el = {
    configBar: document.getElementById('config-bar'),
    cfgAddress: document.getElementById('cfg-address'),
    cfgApikey: document.getElementById('cfg-apikey'),
    cfgConnect: document.getElementById('cfg-connect'),
    cfgDisconnect: document.getElementById('cfg-disconnect'),
    cfgStatus: document.getElementById('cfg-status'),
    statsBar: document.getElementById('stats-bar'),
    statTime: document.getElementById('stat-time'),
    statOnline: document.getElementById('stat-online'),
    statOffline: document.getElementById('stat-offline'),
    statAlert: document.getElementById('stat-alert'),
    search: document.getElementById('search'),
    tableWrap: document.getElementById('table-wrap'),
    userBody: document.getElementById('user-body'),
    emptyState: document.getElementById('empty-state'),
    modal: document.getElementById('detail-modal'),
    detailTitle: document.getElementById('detail-title'),
    detailBody: document.getElementById('detail-body'),
    detailClose: document.getElementById('detail-close'),
    trendModal: document.getElementById('trend-modal'),
    trendTitle: document.getElementById('trend-title'),
    trendClose: document.getElementById('trend-close'),
    trendCanvas: document.getElementById('trend-canvas'),
    rangeBtns: document.querySelectorAll('.range-btn'),
    tabChart: document.getElementById('tab-chart'),
    tabSessions: document.getElementById('tab-sessions'),
    trendChartPane: document.getElementById('trend-chart-pane'),
    trendSessionPane: document.getElementById('trend-session-pane'),
    sessionBody: document.getElementById('session-body')
  };

  var state = {
    users: [],
    connected: false,
    pollTimer: null
  };

  // ---------- 连接 ----------
  el.cfgConnect.addEventListener('click', connect);
  el.cfgDisconnect.addEventListener('click', disconnect);
  el.cfgAddress.addEventListener('keydown', function (e) { if (e.key === 'Enter') connect(); });
  el.cfgApikey.addEventListener('keydown', function (e) { if (e.key === 'Enter') connect(); });

  async function connect() {
    var address = el.cfgAddress.value.trim();
    var apikey = el.cfgApikey.value.trim();
    if (!address || !apikey) { setStatus('地址和 API Key 不能为空', true); return; }
    setStatus('连接中…');
    try {
      var resp = await fetch('/api/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ address: address, apiKey: apikey })
      });
      var data = await resp.json();
      if (data.ok) {
        // 记住地址（API Key 不落盘，安全）
        try { localStorage.setItem('emqxMonitorAddress', address); } catch (e) { }
        state.connected = true;
        setStatus('已连接');
        el.cfgConnect.classList.add('hidden');
        el.cfgDisconnect.classList.remove('hidden');
        el.statsBar.classList.remove('hidden');
        el.tableWrap.classList.remove('hidden');
        el.emptyState.classList.add('hidden');
        startPolling();
      } else {
        setStatus(data.error || '连接失败', true);
      }
    } catch (e) {
      setStatus('请求失败：' + e.message, true);
    }
  }

  async function disconnect() {
    try { await fetch('/api/disconnect', { method: 'POST' }); } catch (e) { }
    state.connected = false;
    stopPolling();
    el.cfgConnect.classList.remove('hidden');
    el.cfgDisconnect.classList.add('hidden');
    el.statsBar.classList.add('hidden');
    el.tableWrap.classList.add('hidden');
    el.emptyState.classList.remove('hidden');
    setStatus('已断开');
    el.userBody.innerHTML = '';
  }

  function setStatus(text, isError) {
    el.cfgStatus.textContent = text;
    el.cfgStatus.style.color = isError ? '#c62828' : '#666';
  }

  // ---------- 轮询 ----------
  function startPolling() {
    stopPolling();
    pollOnce();
    state.pollTimer = setInterval(pollOnce, POLL_MS);
  }
  function stopPolling() {
    if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; }
  }

  async function pollOnce() {
    try {
      var resp = await fetch('/api/snapshot');
      var data = await resp.json();
      if (!data.ok) {
        // 后端轮询失败（如 EMQX 断连），显示错误但不停止前端轮询
        el.userBody.innerHTML = '';
        el.emptyState.classList.remove('hidden');
        el.emptyState.textContent = data.error || '获取数据失败';
        return;
      }
      el.emptyState.classList.add('hidden');
      state.snapshotTime = data.snapshot_time || '';
      state.users = data.users || [];
      render();
    } catch (e) {
      el.emptyState.classList.remove('hidden');
      el.emptyState.textContent = '请求失败：' + e.message;
    }
  }

  // ---------- 渲染 ----------
  var ALERT_TEXT = {
    rate_anomaly: '速率异常',
    flap: '频繁断连',
    no_data: '无数据'
  };
  var ALERT_TAG = {
    rate_anomaly: 'tag-rate',
    flap: 'tag-flap',
    no_data: 'tag-no-data'
  };

  function render() {
    var online = 0, offline = 0, alertCount = 0;
    state.users.forEach(function (u) { if (u.offline) offline++; else online++; alertCount += u.alerts.length; });

    el.statTime.textContent = '快照时间: ' + (state.users.length ? lastSnapshotTime() : '-');
    el.statOnline.textContent = '在线: ' + online;
    el.statOffline.textContent = '离线: ' + offline;
    el.statAlert.textContent = alertCount > 0 ? ('告警: ' + alertCount) : '';

    var keyword = el.search.value.trim().toLowerCase();
    var filtered = state.users.filter(function (u) {
      return !keyword || u.username.toLowerCase().indexOf(keyword) >= 0;
    });

    var html = '';
    filtered.forEach(function (u) {
      html += rowHtml(u);
    });
    el.userBody.innerHTML = html || '<tr><td colspan="8" style="text-align:center;color:#999;">无匹配呼号</td></tr>';
  }

  function lastSnapshotTime() {
    // 从后端 snapshot_time 取（在 state 里存一份）
    return state.snapshotTime || '';
  }

  function rowHtml(u) {
    var rowClass = [];
    var statusHtml, nameHtml, timeHtml = '-', numClass = '';

    if (u.offline) {
      rowClass.push('row-offline');
      statusHtml = '<span class="status-offline"><span class="status-dot"></span>离线</span>';
      timeHtml = u.lastSeenAt ? '最后 ' + u.lastSeenAt.slice(11, 16) : '-';
    } else {
      statusHtml = '<span class="status-online"><span class="status-dot"></span>在线 ' + u.onlineCount + '</span>';
      timeHtml = u.clients && u.clients[0] && u.clients[0].connectedAt
        ? fmtTime(u.clients[0].connectedAt) : '-';
    }

    if (u.alerts.indexOf('rate_anomaly') >= 0) { rowClass.push('row-rate-anomaly'); numClass = 'num'; }
    if (u.alerts.indexOf('flap') >= 0) rowClass.push('row-flap');
    if (u.alerts.indexOf('no_data') >= 0) { rowClass.push('row-no-data'); numClass = 'num'; }

    nameHtml = '<span class="user-name" data-username="' + esc(u.username) + '">' + esc(u.username) + '</span>';

    var tags = u.alerts.map(function (a) {
      return '<span class="alert-tag ' + ALERT_TAG[a] + '">' + ALERT_TEXT[a] + '</span>';
    }).join('');

    return '<tr class="' + rowClass.join(' ') + '">' +
      '<td class="col-name">' + nameHtml + ' ' + tags + '</td>' +
      '<td class="col-status">' + statusHtml + '</td>' +
      '<td class="col-num ' + numClass + '">' + fmtRate(u.rateRecvPps) + '</td>' +
      '<td class="col-num ' + numClass + '">' + fmtRate(u.rateSendPps) + '</td>' +
      '<td class="col-num ' + numClass + '">' + fmtInt(u.totalRecvMsg) + '</td>' +
      '<td class="col-num ' + numClass + '">' + fmtInt(u.totalSendMsg) + '</td>' +
      '<td class="col-time">' + timeHtml + '</td>' +
      '<td class="col-action">' +
        '<button class="btn btn-small" data-trend="' + esc(u.username) + '">趋势</button> ' +
        '<button class="btn btn-small" data-detail="' + esc(u.username) + '">详情</button>' +
      '</td>' +
      '</tr>';
  }

  // ---------- 详情/趋势弹窗 ----------
  el.userBody.addEventListener('click', function (e) {
    var trendBtn = e.target.closest('[data-trend]');
    if (trendBtn) { showTrend(trendBtn.getAttribute('data-trend')); return; }
    var detailBtn = e.target.closest('[data-detail]');
    if (detailBtn) { showDetail(detailBtn.getAttribute('data-detail')); return; }
    var name = e.target.closest('[data-username]');
    if (name) { showDetail(name.getAttribute('data-username')); }
  });
  el.detailClose.addEventListener('click', hideDetail);
  el.modal.addEventListener('click', function (e) { if (e.target === el.modal) hideDetail(); });

  // ---------- 趋势图 ----------
  var trendState = { username: '', range: '1h', tab: 'chart' };
  el.trendClose.addEventListener('click', hideTrend);
  el.trendModal.addEventListener('click', function (e) { if (e.target === el.trendModal) hideTrend(); });
  Array.prototype.forEach.call(el.rangeBtns, function (btn) {
    btn.addEventListener('click', function () {
      trendState.range = btn.getAttribute('data-range');
      Array.prototype.forEach.call(el.rangeBtns, function (b) {
        b.classList.toggle('active', b === btn);
      });
      if (trendState.tab === 'chart') loadTrend(); else loadSessions();
    });
  });
  el.tabChart.addEventListener('click', function () { switchTab('chart'); });
  el.tabSessions.addEventListener('click', function () { switchTab('sessions'); });

  function switchTab(tab) {
    trendState.tab = tab;
    el.tabChart.classList.toggle('active', tab === 'chart');
    el.tabSessions.classList.toggle('active', tab === 'sessions');
    el.trendChartPane.classList.toggle('hidden', tab !== 'chart');
    el.trendSessionPane.classList.toggle('hidden', tab !== 'sessions');
    if (tab === 'chart') loadTrend(); else loadSessions();
  }

  function showTrend(username) {
    trendState.username = username;
    trendState.tab = 'chart';
    el.trendTitle.textContent = '呼号 ' + username + ' — 历史趋势';
    el.trendModal.classList.remove('hidden');
    // 默认选中 1h
    trendState.range = '1h';
    Array.prototype.forEach.call(el.rangeBtns, function (b) {
      b.classList.toggle('active', b.getAttribute('data-range') === '1h');
    });
    el.tabChart.classList.add('active');
    el.tabSessions.classList.remove('active');
    el.trendChartPane.classList.remove('hidden');
    el.trendSessionPane.classList.add('hidden');
    loadTrend();
  }
  function hideTrend() {
    el.trendModal.classList.add('hidden');
  }

  async function loadTrend() {
    var username = encodeURIComponent(trendState.username);
    var range = trendState.range;
    el.trendTitle.textContent = '呼号 ' + trendState.username + ' — 近 ' + range + ' 趋势';
    try {
      var resp = await fetch('/api/history/' + username + '?range=' + range);
      var data = await resp.json();
      if (!data.ok) { drawTrend([], range, data.error || '获取失败'); return; }
      drawTrend(data.points || [], range);
    } catch (e) {
      drawTrend([], range, '请求失败：' + e.message);
    }
  }

  async function loadSessions() {
    var username = encodeURIComponent(trendState.username);
    var range = trendState.range;
    try {
      var resp = await fetch('/api/history/' + username + '/sessions?range=' + range);
      var data = await resp.json();
      var sessions = (data.sessions || []);
      var html = '';
      sessions.forEach(function (s) {
        html += '<tr>' +
          '<td>' + esc(s.start) + '</td>' +
          '<td>' + (s.end ? esc(s.end) : '-') + '</td>' +
          '<td>' + s.duration_min + ' 分钟</td>' +
          '<td>' + (s.online ? '在线中' : '已下线') + '</td>' +
          '</tr>';
      });
      el.sessionBody.innerHTML = html || '<tr><td colspan="4" style="text-align:center;color:#999;">该时间段内无上线记录</td></tr>';
    } catch (e) {
      el.sessionBody.innerHTML = '<tr><td colspan="4" style="text-align:center;color:#999;">请求失败：' + esc(e.message) + '</td></tr>';
    }
  }

  function drawTrend(points, range, errorText) {
    var canvas = el.trendCanvas;
    var ctx = canvas.getContext('2d');
    var W = canvas.width, H = canvas.height;
    var padL = 56, padR = 16, padT = 16, padB = 30;
    ctx.clearRect(0, 0, W, H);
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, W, H);

    if (errorText) {
      ctx.fillStyle = '#999';
      ctx.font = '13px sans-serif';
      ctx.fillText(errorText, padL, H / 2);
      return;
    }
    if (!points.length) {
      ctx.fillStyle = '#999';
      ctx.font = '13px sans-serif';
      ctx.fillText('暂无数据（该时间段内无记录）', padL, H / 2);
      return;
    }

    var send = points.map(function (p) { return p.send_pkt; });
    var recv = points.map(function (p) { return p.recv_pkt; });
    var maxV = Math.max(1, Math.max.apply(null, send.concat(recv)));
    var n = points.length;
    var plotW = W - padL - padR, plotH = H - padT - padB;

    function x(i) { return padL + (n === 1 ? plotW : plotW * i / (n - 1)); }
    function y(v) { return padT + plotH - (plotH * v / maxV); }

    // 网格线
    ctx.strokeStyle = '#e5e5e5';
    ctx.lineWidth = 1;
    for (var g = 0; g <= 4; g++) {
      var gy = padT + plotH * g / 4;
      ctx.beginPath(); ctx.moveTo(padL, gy); ctx.lineTo(W - padR, gy); ctx.stroke();
      ctx.fillStyle = '#999';
      ctx.font = '11px sans-serif';
      ctx.textAlign = 'right';
      ctx.fillText(fmtAxis(Math.round(maxV * (4 - g) / 4)), padL - 8, gy + 4);
    }

    // 时间轴标签（最多 6 个）
    ctx.textAlign = 'center';
    var labelEvery = Math.ceil(n / 6);
    for (var i = 0; i < n; i += labelEvery) {
      ctx.fillStyle = '#999';
      ctx.fillText(fmtTimeLabel(points[i].time, range), x(i), H - 8);
    }
    if (n > 0) {
      ctx.fillStyle = '#999';
      ctx.fillText(fmtTimeLabel(points[n - 1].time, range), x(n - 1), H - 8);
    }

    // 折线
    function drawLine(data, color) {
      ctx.strokeStyle = color;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      for (var j = 0; j < n; j++) {
        var px = x(j), py = y(data[j]);
        if (j === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
      }
      ctx.stroke();
    }
    drawLine(send, '#1565c0');   // 发包 蓝
    drawLine(recv, '#2e7d32');   // 收包 绿
  }

  function fmtAxis(v) {
    if (v >= 1000000) return (v / 1000000).toFixed(1) + 'M';
    if (v >= 1000) return (v / 1000).toFixed(1) + 'k';
    return String(v);
  }
  function fmtTimeLabel(t, range) {
    // t = "yyyy-MM-dd HH:mm"
    if (range === '24h') return t.slice(5, 16);   // MM-dd HH:mm
    return t.slice(11, 16);                        // HH:mm
  }

  function showDetail(username) {
    var u = state.users.find(function (x) { return x.username === username; });
    if (!u) return;
    currentDetailUsername = username;
    el.detailTitle.textContent = '呼号 ' + username + ' — 客户端详情';
    var html = '';
    (u.clients || []).forEach(function (c) {
      html += '<tr>' +
        '<td>' + esc(c.clientId) + '</td>' +
        '<td>' + (c.connected ? '在线' : '离线') + '</td>' +
        '<td>' + esc(c.ipAddress || '-') + '</td>' +
        '<td>' + fmtInt(c.recvPkt) + '</td>' +
        '<td>' + fmtInt(c.sendPkt) + '</td>' +
        '<td>' + fmtInt(c.recvMsg) + '</td>' +
        '<td>' + fmtInt(c.sendMsg) + '</td>' +
        '<td>' + (c.connectedAt ? fmtTime(c.connectedAt) : '-') + '</td>' +
        '</tr>';
    });
    el.detailBody.innerHTML = html || '<tr><td colspan="8" style="text-align:center;color:#999;">无客户端记录</td></tr>';
    el.modal.classList.remove('hidden');
  }
  function hideDetail() {
    el.modal.classList.add('hidden');
    currentDetailUsername = null;
  }

  // ---------- 工具 ----------
  function fmtInt(n) {
    if (n === undefined || n === null) return '-';
    return n.toLocaleString('en-US');
  }
  function fmtRate(r) {
    if (r === undefined || r === null) return '-';
    return r.toFixed(2);
  }
  function fmtTime(iso) {
    // RFC3339（如 2026-08-01T04:05:12.123+00:00）→ 本地时间 HH:mm:ss
    var d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    var p = function (n) { return n < 10 ? '0' + n : '' + n; };
    return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
  }
  function esc(s) {
    return String(s === undefined || s === null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  // 启动时恢复：记住的地址 + 已连接状态
  try {
    var saved = localStorage.getItem('emqxMonitorAddress');
    if (saved) el.cfgAddress.value = saved;
  } catch (e) { }
  fetch('/api/status').then(function (r) { return r.json(); }).then(function (d) {
    if (d.configured) {
      state.connected = true;
      el.cfgConnect.classList.add('hidden');
      el.cfgDisconnect.classList.remove('hidden');
      el.statsBar.classList.remove('hidden');
      el.tableWrap.classList.remove('hidden');
      el.emptyState.classList.add('hidden');
      setStatus('已连接');
      startPolling();
    }
  }).catch(function () { });
})();
