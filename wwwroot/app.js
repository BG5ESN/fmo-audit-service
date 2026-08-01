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
    detailClose: document.getElementById('detail-close')
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
      '<td class="col-action"><button class="btn btn-small" data-detail="' + esc(u.username) + '">详情</button></td>' +
      '</tr>';
  }

  // ---------- 详情弹窗 ----------
  el.userBody.addEventListener('click', function (e) {
    var detailBtn = e.target.closest('[data-detail]');
    if (detailBtn) { showDetail(detailBtn.getAttribute('data-detail')); return; }
    var name = e.target.closest('[data-username]');
    if (name) { showDetail(name.getAttribute('data-username')); }
  });
  el.detailClose.addEventListener('click', hideDetail);
  el.modal.addEventListener('click', function (e) { if (e.target === el.modal) hideDetail(); });

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

  // 启动时尝试恢复连接状态
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
