/* ============================================================
   S3 对象存储 · 管理控制台（原生 JS，无构建步骤）
   ============================================================ */
(() => {
  'use strict';

  // ---------------------------------------------------------- 工具

  const $ = (selector, root = document) => root.querySelector(selector);
  const byId = (id) => document.getElementById(id);

  const esc = (value) => String(value ?? '').replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[ch]));

  const ICONS = {
    grid: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z"/></svg>',
    bucket: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 7.5c0-1.4 3.1-2.5 7-2.5s7 1.1 7 2.5-3.1 2.5-7 2.5-7-1.1-7-2.5Z"/><path d="M5 7.5v9c0 1.4 3.1 2.5 7 2.5s7-1.1 7-2.5v-9"/></svg>',
    key: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="8" cy="15" r="4"/><path d="m11 12 8-8M17 4h3v3"/></svg>',
    refresh: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5"/></svg>',
    logout: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4M10 17l5-5-5-5M15 12H3"/></svg>',
    plus: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg>',
    upload: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 16V4m0 0L7 9m5-5 5 5M4 20h16"/></svg>',
    download: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4v12m0 0 5-5m-5 5-5-5M4 20h16"/></svg>',
    trash: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M9 7V5h6v2M6 7l1 13h10l1-13M10 11v6M14 11v6"/></svg>',
    folder: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Z"/></svg>',
    file: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8l-5-5Z"/><path d="M14 3v5h5"/></svg>',
    search: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>',
    chevron: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6"/></svg>',
    x: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg>',
    copy: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/></svg>',
    cloud: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 18a4 4 0 0 1 .8-7.9A5.5 5.5 0 0 1 18.5 10 4 4 0 0 1 18 18H7Z"/></svg>',
    database: '<svg viewBox="0 0 24 24" aria-hidden="true"><ellipse cx="12" cy="6" rx="7" ry="3"/><path d="M5 6v12c0 1.7 3.1 3 7 3s7-1.3 7-3V6"/></svg>',
    alert: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4 3 20h18L12 4Z"/><path d="M12 10v4M12 17.4v.6"/></svg>',
    clock: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="8"/><path d="M12 8v4l3 2"/></svg>',
    layers: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m12 3 9 5-9 5-9-5 9-5Z"/><path d="m3 13 9 5 9-5"/></svg>',
  };

  const icon = (name) => ICONS[name] || '';

  const fmtBytes = (bytes) => {
    const value = Number(bytes);
    if (!Number.isFinite(value) || value < 0) return '—';
    if (value === 0) return '0 B';
    const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
    let size = value;
    let index = 0;
    while (size >= 1024 && index < units.length - 1) {
      size /= 1024;
      index += 1;
    }
    const digits = index === 0 ? 0 : size >= 100 ? 0 : size >= 10 ? 1 : 2;
    return `${size.toFixed(digits)} ${units[index]}`;
  };

  const fmtNumber = (value) => Number(value ?? 0).toLocaleString('zh-CN');

  const fmtDate = (iso) => {
    if (!iso) return '—';
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return '—';
    const pad = (n) => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
  };

  const fmtDuration = (seconds) => {
    const total = Math.max(0, Math.floor(Number(seconds) || 0));
    const days = Math.floor(total / 86400);
    const hours = Math.floor((total % 86400) / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    if (days > 0) return `${days} 天 ${hours} 小时`;
    if (hours > 0) return `${hours} 小时 ${minutes} 分`;
    return `${minutes} 分钟`;
  };

  const encPath = (path) => path.split('/').map(encodeURIComponent).join('/');
  const encBucket = (name) => encodeURIComponent(name);

  // ---------------------------------------------------------- 状态

  const state = {
    me: null,
    route: { name: 'overview' },
    listing: null,
    selection: new Set(),
    uploads: [],
    loginTab: 'token',
    renderToken: 0,
  };

  // ---------------------------------------------------------- API

  async function api(path, options = {}) {
    const response = await fetch('/admin/api' + path, {
      credentials: 'same-origin',
      ...options,
    });

    if (response.status === 401) {
      showLogin();
      throw new Error('登录状态已失效，请重新登录。');
    }

    if (!response.ok) {
      let message = `请求失败（HTTP ${response.status}）`;
      try {
        const payload = await response.json();
        if (payload && payload.error) message = payload.error;
      } catch { /* 非 JSON 响应，保留默认信息 */ }
      throw new Error(message);
    }

    if (response.status === 204) return null;
    const contentType = response.headers.get('content-type') || '';
    return contentType.includes('application/json') ? response.json() : response.text();
  }

  const jsonRequest = (method, body) => ({
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  // ---------------------------------------------------------- 提示 / 弹窗

  function toast(message, type = 'ok') {
    const node = document.createElement('div');
    node.className = `toast toast--${type}`;
    node.innerHTML = `<span class="dot"></span><span>${esc(message)}</span>`;
    byId('toasts').append(node);

    setTimeout(() => {
      node.style.transition = 'opacity .18s ease, transform .18s ease';
      node.style.opacity = '0';
      node.style.transform = 'translateY(4px)';
      setTimeout(() => node.remove(), 200);
    }, 3200);
  }

  function openModal({ title, body, footer = '', wide = false, onMount }) {
    const root = byId('modal-root');
    root.innerHTML = `
      <div class="modal-backdrop" data-act="close-modal">
        <div class="modal${wide ? ' modal--wide' : ''}" role="dialog" aria-modal="true" aria-label="${esc(title)}">
          <div class="modal__head">
            <h3>${esc(title)}</h3>
            <button class="icon-btn" data-act="close-modal" title="关闭">${icon('x')}</button>
          </div>
          <div class="modal__body">${body}</div>
          ${footer ? `<div class="modal__foot">${footer}</div>` : ''}
        </div>
      </div>`;
    $('.modal', root).addEventListener('click', (event) => event.stopPropagation());
    if (onMount) onMount(root);
    const focusTarget = $('input, select, textarea, button', $('.modal__body', root));
    if (focusTarget) focusTarget.focus();
  }

  function closeModal() {
    byId('modal-root').innerHTML = '';
  }

  function confirmModal({ title, message, confirmText = '确认删除', onConfirm }) {
    openModal({
      title,
      body: `<p style="margin:0;color:var(--text-2);line-height:1.7">${esc(message)}</p>`,
      footer: `
        <button class="btn" data-act="close-modal">取消</button>
        <button class="btn btn--danger" data-act="confirm-modal">${esc(confirmText)}</button>`,
      onMount: (root) => {
        $('[data-act="confirm-modal"]', root).addEventListener('click', async () => {
          const button = $('[data-act="confirm-modal"]', root);
          button.disabled = true;
          try {
            await onConfirm();
            closeModal();
          } catch (error) {
            button.disabled = false;
            toast(error.message, 'error');
          }
        });
      },
    });
  }

  // ---------------------------------------------------------- 登录

  function showLogin() {
    state.me = null;
    byId('shell').hidden = true;
    byId('login').hidden = false;
    byId('login-endpoint').textContent = location.origin;
  }

  function boot(me) {
    state.me = me;
    byId('login').hidden = true;
    byId('shell').hidden = false;
    byId('endpoint-chip').textContent = location.origin.replace(/^https?:\/\//, '');
    byId('userchip').innerHTML = `
      <span class="userchip__avatar">${esc((me.displayName || me.subject || 'A').slice(0, 1).toUpperCase())}</span>
      <span>${esc(me.displayName || me.subject)}</span>`;
    applyRoute();
  }

  function bindLogin() {
    byId('login-tabs').addEventListener('click', (event) => {
      const tab = event.target.closest('[data-tab]');
      if (!tab) return;
      state.loginTab = tab.dataset.tab;
      document.querySelectorAll('#login-tabs .tab').forEach((node) => {
        node.classList.toggle('is-active', node.dataset.tab === state.loginTab);
      });
      document.querySelectorAll('#login-form [data-pane]').forEach((pane) => {
        pane.hidden = pane.dataset.pane !== state.loginTab;
      });
      byId('login-error').hidden = true;
    });

    byId('login-form').addEventListener('submit', async (event) => {
      event.preventDefault();
      const errorBox = byId('login-error');
      const submit = byId('login-submit');
      errorBox.hidden = true;
      submit.disabled = true;
      submit.textContent = '登录中…';

      const payload = state.loginTab === 'token'
        ? { token: byId('login-token').value.trim() }
        : {
            accessKeyId: byId('login-ak').value.trim(),
            secretAccessKey: byId('login-sk').value,
          };

      try {
        const response = await fetch('/admin/api/auth/login', {
          method: 'POST',
          credentials: 'same-origin',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        });

        if (!response.ok) {
          let message = '登录失败，请检查凭证。';
          try {
            const data = await response.json();
            if (data && data.error) message = data.error;
          } catch { /* ignore */ }
          throw new Error(message);
        }

        const me = await response.json();
        byId('login-token').value = '';
        byId('login-sk').value = '';
        if (!location.hash || location.hash === '#/') location.hash = '#/overview';
        boot(me);
        toast(`欢迎回来，${me.displayName || me.subject}`);
      } catch (error) {
        errorBox.textContent = error.message;
        errorBox.hidden = false;
        submit.disabled = false;
        submit.textContent = '登录';
      }
    });
  }

  // ---------------------------------------------------------- 路由

  function parseRoute() {
    const raw = (location.hash || '#/overview').replace(/^#/, '');
    const [pathPart, queryPart = ''] = raw.split('?');
    const segments = pathPart.split('/').filter(Boolean);
    const query = new URLSearchParams(queryPart);

    if (segments[0] === 'buckets' && segments[1]) {
      return { name: 'bucket', bucket: decodeURIComponent(segments[1]), prefix: query.get('prefix') || '' };
    }
    if (segments[0] === 'buckets') return { name: 'buckets' };
    if (segments[0] === 'keys') return { name: 'keys' };
    return { name: 'overview' };
  }

  function applyRoute() {
    if (!state.me) return;
    state.route = parseRoute();
    state.selection = new Set();
    render();
  }

  function navigate(hash) {
    if (location.hash === hash) {
      applyRoute();
    } else {
      location.hash = hash;
    }
  }

  function setNav(name) {
    document.querySelectorAll('.nav__item[data-nav]').forEach((node) => {
      node.classList.toggle('is-active', node.dataset.nav === name);
    });
  }

  function setCrumb(parts) {
    byId('crumb').innerHTML = parts
      .map((part, index) => {
        const isLast = index === parts.length - 1;
        const label = isLast ? `<b>${esc(part.label)}</b>` : `<a href="${esc(part.href || '#')}">${esc(part.label)}</a>`;
        return index === 0 ? label : `<span class="sep">/</span>${label}`;
      })
      .join('');
  }

  // ---------------------------------------------------------- 渲染

  async function render() {
    const token = ++state.renderToken;
    const route = state.route;

    if (route.name === 'overview') {
      setNav('overview');
      setCrumb([{ label: '概览' }]);
    } else if (route.name === 'buckets') {
      setNav('buckets');
      setCrumb([{ label: '存储桶' }]);
    } else if (route.name === 'bucket') {
      setNav('buckets');
      setCrumb([
        { label: '存储桶', href: '#/buckets' },
        { label: route.bucket },
      ]);
    } else {
      setNav('keys');
      setCrumb([{ label: '访问密钥' }]);
    }

    const view = byId('view');
    view.innerHTML = `<div class="loading"><span class="spinner"></span><span>加载中…</span></div>`;

    try {
      if (route.name === 'overview') await renderOverview(view, token);
      else if (route.name === 'buckets') await renderBuckets(view, token);
      else if (route.name === 'bucket') await renderBucket(view, token, route);
      else await renderKeys(view, token);
    } catch (error) {
      if (token !== state.renderToken) return;
      view.innerHTML = errorState(error.message);
    }
  }

  function errorState(message) {
    return `
      <div class="empty">
        ${icon('alert')}
        <strong>加载失败</strong>
        <p>${esc(message || '未知错误')}</p>
        <button class="btn btn--sm" data-act="refresh" style="margin-top:6px">重试</button>
      </div>`;
  }

  // ---------------------------------------------------------- 概览

  async function renderOverview(view, token) {
    const data = await api('/overview');
    if (token !== state.renderToken) return;

    const disk = data.disk || {};
    const usedPercent = disk.available && disk.totalBytes
      ? Math.min(100, (disk.usedBytes / disk.totalBytes) * 100)
      : 0;
    const server = data.server || {};
    const metrics = data.metrics || {};

    view.innerHTML = `
      <div class="page-head">
        <div>
          <h1>概览</h1>
          <p>实例容量、流量与运行状态</p>
        </div>
        <div class="page-head__actions">
          <button class="btn" data-act="refresh">${icon('refresh')}刷新</button>
          <button class="btn btn--primary" data-act="create-bucket">${icon('plus')}新建存储桶</button>
        </div>
      </div>

      <div class="stats">
        <div class="stat">
          <span class="stat__icon">${icon('bucket')}</span>
          <div>
            <div class="stat__label">存储桶</div>
            <div class="stat__value">${fmtNumber(data.buckets)}</div>
            <div class="stat__sub">${fmtNumber(data.accessKeys)} 个访问密钥</div>
          </div>
        </div>
        <div class="stat">
          <span class="stat__icon stat__icon--slate">${icon('layers')}</span>
          <div>
            <div class="stat__label">对象总数</div>
            <div class="stat__value">${fmtNumber(data.objects)}</div>
            <div class="stat__sub">跨全部存储桶</div>
          </div>
        </div>
        <div class="stat">
          <span class="stat__icon stat__icon--ok">${icon('database')}</span>
          <div>
            <div class="stat__label">已用容量</div>
            <div class="stat__value">${fmtBytes(data.totalBytes)}</div>
            <div class="stat__sub">磁盘剩余 ${disk.available ? fmtBytes(disk.freeBytes) : '未知'}</div>
          </div>
        </div>
        <div class="stat">
          <span class="stat__icon stat__icon--warn">${icon('clock')}</span>
          <div>
            <div class="stat__label">进行中分片</div>
            <div class="stat__value">${fmtNumber(data.activeUploads)}</div>
            <div class="stat__sub">暂存 ${fmtBytes(data.pendingUploadBytes)}</div>
          </div>
        </div>
      </div>

      <div class="grid-2">
        <section class="card">
          <div class="card__head">
            <h2>磁盘与数据目录</h2>
            <span class="badge ${disk.available ? 'badge--ok' : 'badge--warn'}">
              <span class="dot"></span>${disk.available ? '在线' : '未知'}
            </span>
          </div>
          <div class="card__body">
            <div class="meter">
              <div class="meter__head">
                <span>磁盘使用率</span>
                <span>${disk.available ? usedPercent.toFixed(1) + '%' : '—'}</span>
              </div>
              <div class="meter__bar">
                <div class="meter__fill" style="width:${usedPercent.toFixed(1)}%"></div>
              </div>
              <div class="meter__legend">
                <span>已用 ${disk.available ? fmtBytes(disk.usedBytes) : '—'}</span>
                <span>总量 ${disk.available ? fmtBytes(disk.totalBytes) : '—'}</span>
              </div>
            </div>
            <div class="kv" style="margin-top:12px">
              <div class="kv__row"><span>存储桶 / 对象</span><span>${fmtNumber(data.buckets)} / ${fmtNumber(data.objects)}</span></div>
              <div class="kv__row"><span>对象存储占用</span><span>${fmtBytes(data.totalBytes)}</span></div>
              <div class="kv__row"><span>分片暂存</span><span>${fmtBytes(data.pendingUploadBytes)}</span></div>
              <div class="kv__row"><span>数据目录</span><span title="${esc(server.rootPath)}">${esc(server.rootPath || '—')}</span></div>
              <div class="kv__row"><span>S3 端点</span><span>${esc(server.endpoint || '—')}</span></div>
            </div>
          </div>
        </section>

        <section class="card">
          <div class="card__head">
            <h2>运行状态</h2>
            <span class="badge badge--primary">v${esc(server.version || '—')}</span>
          </div>
          <div class="card__body">
            <div class="kv">
              <div class="kv__row"><span>运行时长</span><span>${fmtDuration(server.uptimeSeconds)}</span></div>
              <div class="kv__row"><span>区域</span><span>${esc(server.region || '—')}</span></div>
              <div class="kv__row"><span>运行时</span><span>.NET ${esc(server.runtime || '—')}</span></div>
              <div class="kv__row"><span>内存占用</span><span>${fmtBytes(server.workingSetBytes)}</span></div>
              <div class="kv__row"><span>累计请求</span><span>${fmtNumber(metrics.requests)}</span></div>
              <div class="kv__row">
                <span>错误请求</span>
                <span>${fmtNumber(metrics.clientErrors)} × 4xx / ${fmtNumber(metrics.serverErrors)} × 5xx</span>
              </div>
              <div class="kv__row">
                <span>流量</span>
                <span>入 ${fmtBytes(metrics.bytesIn)} / 出 ${fmtBytes(metrics.bytesOut)}</span>
              </div>
            </div>
          </div>
        </section>
      </div>

      <section class="card">
        <div class="card__head">
          <h2>存储桶</h2>
          <a class="link-btn" href="#/buckets">查看全部</a>
        </div>
        <div class="card__body card__body--flush">
          ${bucketTable(data.bucketSummaries || [], { compact: true })}
        </div>
      </section>`;
  }

  // ---------------------------------------------------------- 存储桶列表

  function bucketTable(buckets, { compact = false } = {}) {
    if (!buckets.length) {
      return `
        <div class="empty">
          ${icon('bucket')}
          <strong>还没有存储桶</strong>
          <p>创建第一个存储桶后即可开始上传对象，也可以用任何 S3 客户端写入。</p>
          <button class="btn btn--primary btn--sm" data-act="create-bucket" style="margin-top:6px">新建存储桶</button>
        </div>`;
    }

    const rows = buckets.map((bucket) => `
      <tr class="is-clickable" data-act="open-bucket" data-name="${esc(bucket.name)}">
        <td>
          <div class="cell-name">
            <span class="ico">${icon('bucket')}</span>
            <span class="txt">
              <strong>${esc(bucket.name)}</strong>
              <small>${esc(bucket.region || '—')}</small>
            </span>
          </div>
        </td>
        <td class="muted">${esc(bucket.ownerDisplayName || bucket.ownerId || '—')}</td>
        <td class="num">${fmtNumber(bucket.objectCount)}</td>
        <td class="num">${fmtBytes(bucket.totalSize)}</td>
        <td class="muted">${fmtDate(bucket.createdAt)}</td>
        ${compact ? '' : `
        <td class="actions">
          <button class="icon-btn" data-act="open-bucket" data-name="${esc(bucket.name)}" title="浏览对象">${icon('chevron')}</button>
          <button class="icon-btn danger" data-act="delete-bucket" data-name="${esc(bucket.name)}" data-count="${bucket.objectCount}" title="删除存储桶">${icon('trash')}</button>
        </td>`}
      </tr>`).join('');

    return `
      <div class="table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th>名称</th>
              <th>拥有者</th>
              <th class="num">对象数</th>
              <th class="num">容量</th>
              <th>创建时间</th>
              ${compact ? '' : '<th class="actions">操作</th>'}
            </tr>
          </thead>
          <tbody>${rows}</tbody>
        </table>
      </div>`;
  }

  async function renderBuckets(view, token) {
    const data = await api('/buckets');
    if (token !== state.renderToken) return;

    view.innerHTML = `
      <div class="page-head">
        <div>
          <h1>存储桶</h1>
          <p>共 ${fmtNumber((data.buckets || []).length)} 个存储桶，按名称排序</p>
        </div>
        <div class="page-head__actions">
          <button class="btn" data-act="refresh">${icon('refresh')}刷新</button>
          <button class="btn btn--primary" data-act="create-bucket">${icon('plus')}新建存储桶</button>
        </div>
      </div>
      <section class="card">
        <div class="card__body card__body--flush">${bucketTable(data.buckets || [])}</div>
      </section>`;
  }

  // ---------------------------------------------------------- 桶详情

  async function renderBucket(view, token, route) {
    const bucket = route.bucket;
    const prefix = route.prefix;
    const query = new URLSearchParams({ prefix, delimiter: '/', maxKeys: '200' });

    const [meta, listing, uploads] = await Promise.all([
      api(`/buckets/${encBucket(bucket)}`),
      api(`/buckets/${encBucket(bucket)}/objects?${query.toString()}`),
      api(`/buckets/${encBucket(bucket)}/uploads`),
    ]);

    if (token !== state.renderToken) return;

    state.listing = listing;
    state.selection = new Set();

    view.innerHTML = `
      <div class="page-head">
        <div>
          <h1>${esc(bucket)}</h1>
          <p>
            ${fmtNumber(meta.objectCount)} 个对象 · ${fmtBytes(meta.totalSize)}
            · 拥有者 ${esc(meta.ownerDisplayName || meta.ownerId)}
            · 创建于 ${fmtDate(meta.createdAt)}
          </p>
        </div>
        <div class="page-head__actions">
          <button class="btn btn--danger" data-act="delete-bucket" data-name="${esc(bucket)}" data-count="${meta.objectCount}">${icon('trash')}删除桶</button>
          <button class="btn btn--primary" data-act="upload">${icon('upload')}上传文件</button>
        </div>
      </div>

      <section class="card">
        <div class="toolbar">
          ${breadcrumb(bucket, prefix)}
          <div class="toolbar__right">
            <div class="search">
              ${icon('search')}
              <input id="obj-filter" type="search" placeholder="筛选当前列表" autocomplete="off">
            </div>
            <button class="btn btn--sm" data-act="refresh-listing">${icon('refresh')}刷新</button>
            <button class="btn btn--sm" data-act="delete-selected" id="delete-selected" disabled>${icon('trash')}删除选中</button>
          </div>
        </div>

        <div class="dropzone" id="dropzone">
          ${icon('cloud')}
          <strong>拖拽文件到这里上传</strong>
          <p>也可以点击右上角「上传文件」，支持一次选择多个文件；上传到当前目录</p>
        </div>

        <div id="obj-table">${objectTable(listing)}</div>
      </section>

      <section class="card" style="margin-top:16px">
        <div class="card__head">
          <h2>未完成的分片上传</h2>
          <span class="badge ${uploads.uploads.length ? 'badge--warn' : ''}">${uploads.uploads.length}</span>
        </div>
        <div class="card__body card__body--flush">${uploadsTable(uploads.uploads || [])}</div>
      </section>

      <input type="file" id="file-input" multiple hidden>`;
  }

  function breadcrumb(bucket, prefix) {
    const parts = prefix.split('/').filter(Boolean);
    const nodes = [`<button data-act="goto-prefix" data-prefix="" class="${parts.length ? '' : 'is-last'}">${esc(bucket)}</button>`];

    parts.forEach((part, index) => {
      const target = parts.slice(0, index + 1).join('/') + '/';
      const isLast = index === parts.length - 1;
      nodes.push('<span class="sep">/</span>');
      nodes.push(`<button data-act="goto-prefix" data-prefix="${esc(target)}" class="${isLast ? 'is-last' : ''}">${esc(part)}</button>`);
    });

    return `<div class="breadcrumb">${nodes.join('')}</div>`;
  }

  function objectTable(listing) {
    const folders = (listing.commonPrefixes || []).map((prefix) => `
      <tr class="is-clickable" data-act="goto-prefix" data-prefix="${esc(prefix)}">
        <td style="width:34px"></td>
        <td>
          <div class="cell-name">
            <span class="ico ico--folder">${icon('folder')}</span>
            <span class="txt"><strong>${esc(prefix.slice(listing.prefix.length))}</strong><small>目录</small></span>
          </div>
        </td>
        <td class="num faint">—</td>
        <td class="muted">—</td>
        <td class="mono faint">—</td>
        <td class="actions"></td>
      </tr>`).join('');

    const objects = (listing.objects || []).map((object) => {
      const base = object.key.slice(listing.prefix.length);
      const url = `/admin/api/buckets/${encBucket(listing.bucket)}/objects/${encPath(object.key)}?download=1`;
      return `
      <tr>
        <td style="width:34px">
          <input class="checkbox" type="checkbox" data-select="${esc(object.key)}" aria-label="选择 ${esc(object.key)}">
        </td>
        <td>
          <div class="cell-name">
            <span class="ico">${icon('file')}</span>
            <span class="txt">
              <strong title="${esc(object.key)}">${esc(base)}</strong>
              <small>${esc(object.contentType)}</small>
            </span>
          </div>
        </td>
        <td class="num">${fmtBytes(object.size)}</td>
        <td class="muted">${fmtDate(object.lastModified)}</td>
        <td class="mono faint" title="${esc(object.etag)}">${esc(String(object.etag).slice(0, 12))}…</td>
        <td class="actions">
          <a class="icon-btn" href="${esc(url)}" title="下载">${icon('download')}</a>
          <button class="icon-btn danger" data-act="delete-object" data-key="${esc(object.key)}" title="删除">${icon('trash')}</button>
        </td>
      </tr>`;
    }).join('');

    if (!folders && !objects) {
      return `
        <div class="empty">
          ${icon('file')}
          <strong>这个目录还是空的</strong>
          <p>拖拽文件到上方区域，或点击「上传文件」写入第一个对象。</p>
        </div>`;
    }

    return `
      <div class="table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th style="width:34px"></th>
              <th>名称</th>
              <th class="num">大小</th>
              <th>修改时间</th>
              <th>ETag</th>
              <th class="actions">操作</th>
            </tr>
          </thead>
          <tbody>${folders}${objects}</tbody>
        </table>
      </div>
      ${listing.isTruncated
        ? `<div class="pager"><button class="btn btn--sm" data-act="load-more">加载更多</button></div>`
        : ''}`;
  }

  function uploadsTable(uploads) {
    if (!uploads.length) {
      return `<div class="empty" style="padding:34px 20px">
        ${icon('clock')}
        <strong>没有进行中的分片上传</strong>
        <p>中断的大文件上传会出现在这里，可以手动终止以释放临时空间。</p>
      </div>`;
    }

    const rows = uploads.map((upload) => `
      <tr>
        <td>
          <div class="cell-name">
            <span class="ico">${icon('file')}</span>
            <span class="txt"><strong>${esc(upload.key)}</strong><small>${esc(upload.uploadId)}</small></span>
          </div>
        </td>
        <td class="num">${fmtNumber(upload.uploadedParts)}</td>
        <td class="num">${fmtBytes(upload.uploadedBytes)}</td>
        <td class="muted">${fmtDate(upload.initiatedAt)}</td>
        <td class="actions">
          <button class="btn btn--sm btn--danger" data-act="abort-upload" data-id="${esc(upload.uploadId)}">终止</button>
        </td>
      </tr>`).join('');

    return `
      <div class="table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th>对象</th>
              <th class="num">已传分片</th>
              <th class="num">已传大小</th>
              <th>发起时间</th>
              <th class="actions">操作</th>
            </tr>
          </thead>
          <tbody>${rows}</tbody>
        </table>
      </div>`;
  }

  // ---------------------------------------------------------- 访问密钥

  async function renderKeys(view, token) {
    const data = await api('/access-keys');
    if (token !== state.renderToken) return;

    const keys = data.accessKeys || [];
    const rows = keys.map((key) => `
      <tr>
        <td>
          <div class="cell-name">
            <span class="ico">${icon('key')}</span>
            <span class="txt">
              <strong>${esc(key.displayName || key.accessKeyId)}${key.isCurrent ? ' <span class="badge badge--primary">当前会话</span>' : ''}</strong>
              <small>${esc(key.accessKeyId)}</small>
            </span>
          </div>
        </td>
        <td>${key.isAdmin ? '<span class="badge badge--primary">管理员</span>' : '<span class="badge">普通</span>'}</td>
        <td>
          <span class="badge ${key.enabled ? 'badge--ok' : 'badge--danger'}">
            <span class="dot"></span>${key.enabled ? '启用' : '禁用'}
          </span>
        </td>
        <td class="muted">${fmtDate(key.createdAt)}</td>
        <td class="actions">
          <button class="btn btn--sm" data-act="toggle-key" data-id="${esc(key.accessKeyId)}" data-enabled="${key.enabled}">
            ${key.enabled ? '禁用' : '启用'}
          </button>
          <button class="btn btn--sm" data-act="rotate-key" data-id="${esc(key.accessKeyId)}" data-name="${esc(key.displayName || key.accessKeyId)}" title="轮换 Secret">轮换</button>
          <button class="icon-btn danger" data-act="delete-key" data-id="${esc(key.accessKeyId)}" data-name="${esc(key.displayName || key.accessKeyId)}" title="删除">${icon('trash')}</button>
        </td>
      </tr>`).join('');

    view.innerHTML = `
      <div class="page-head">
        <div>
          <h1>访问密钥</h1>
          <p>用于 S3 客户端（aws cli / mc / SDK）签名，管理员密钥还可以登录本控制台</p>
        </div>
        <div class="page-head__actions">
          <button class="btn" data-act="refresh">${icon('refresh')}刷新</button>
          <button class="btn btn--primary" data-act="create-key">${icon('plus')}新建密钥</button>
        </div>
      </div>

      <section class="card">
        <div class="card__body card__body--flush">
          ${keys.length ? `
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th>名称 / AccessKeyId</th>
                  <th>权限</th>
                  <th>状态</th>
                  <th>创建时间</th>
                  <th class="actions">操作</th>
                </tr>
              </thead>
              <tbody>${rows}</tbody>
            </table>
          </div>` : `
          <div class="empty">
            ${icon('key')}
            <strong>还没有访问密钥</strong>
            <p>创建密钥后即可用 aws cli、mc 或任意 S3 SDK 访问本服务。</p>
            <button class="btn btn--primary btn--sm" data-act="create-key" style="margin-top:6px">新建密钥</button>
          </div>`}
        </div>
      </section>

      <p class="login__hint" style="margin-top:14px">
        SecretAccessKey 只在创建时显示一次，服务端仅保存用于签名的明文（SigV4 需要），请妥善保管。
      </p>`;
  }

  // ---------------------------------------------------------- 弹窗交互

  function openCreateBucket() {
    openModal({
      title: '新建存储桶',
      body: `
        <div class="field">
          <label for="bucket-name">桶名称</label>
          <input id="bucket-name" type="text" placeholder="例如 photos、backup-2026" spellcheck="false">
        </div>
        <p class="login__hint" style="margin:0">
          3-63 位，只能包含小写字母、数字、<code>-</code> 与 <code>.</code>，必须以字母或数字开头结尾。
        </p>`,
      footer: `
        <button class="btn" data-act="close-modal">取消</button>
        <button class="btn btn--primary" data-act="submit-create-bucket">创建</button>`,
      onMount: (root) => {
        $('[data-act="submit-create-bucket"]', root).addEventListener('click', async () => {
          const input = byId('bucket-name');
          const name = input.value.trim();
          if (!name) {
            toast('请输入桶名称', 'error');
            return;
          }

          const button = $('[data-act="submit-create-bucket"]', root);
          button.disabled = true;
          try {
            await api('/buckets', jsonRequest('POST', { name }));
            closeModal();
            toast(`存储桶 ${name} 已创建`);
            navigate(`#/buckets/${encBucket(name)}`);
          } catch (error) {
            button.disabled = false;
            toast(error.message, 'error');
          }
        });

        byId('bucket-name').addEventListener('keydown', (event) => {
          if (event.key === 'Enter') {
            event.preventDefault();
            $('[data-act="submit-create-bucket"]', root).click();
          }
        });
      },
    });
  }

  function openCreateKey() {
    openModal({
      title: '新建访问密钥',
      body: `
        <div class="field">
          <label for="key-name">显示名称</label>
          <input id="key-name" type="text" placeholder="例如 ci-pipeline、backup" autocomplete="off">
        </div>
        <label class="check-row">
          <input type="checkbox" id="key-admin">
          <span>授予管理员权限（可访问所有桶，并可登录本控制台）</span>
        </label>`,
      footer: `
        <button class="btn" data-act="close-modal">取消</button>
        <button class="btn btn--primary" data-act="submit-create-key">创建密钥</button>`,
      onMount: (root) => {
        $('[data-act="submit-create-key"]', root).addEventListener('click', async () => {
          const button = $('[data-act="submit-create-key"]', root);
          button.disabled = true;
          try {
            const created = await api('/access-keys', jsonRequest('POST', {
              displayName: byId('key-name').value.trim(),
              isAdmin: byId('key-admin').checked,
            }));
            render();
            showSecret(created);
            toast('访问密钥已创建');
          } catch (error) {
            button.disabled = false;
            toast(error.message, 'error');
          }
        });
      },
    });
  }

  function showSecret(created, title = '密钥创建成功') {
    openModal({
      title,
      wide: true,
      body: `
        <div class="notice">SecretAccessKey 只显示这一次，关闭窗口后无法再次查看。请立即复制保存。</div>
        <div class="field">
          <label>AccessKeyId</label>
          <div class="secret-box"><span>${esc(created.accessKeyId)}</span>
            <button data-act="copy" data-copy="${esc(created.accessKeyId)}">复制</button>
          </div>
        </div>
        <div class="field">
          <label>SecretAccessKey</label>
          <div class="secret-box"><span>${esc(created.secretAccessKey)}</span>
            <button data-act="copy" data-copy="${esc(created.secretAccessKey)}">复制</button>
          </div>
        </div>
        <p class="login__hint" style="margin:0">客户端接入示例（把 <code>&lt;secret&gt;</code> 换成上面的 SecretAccessKey）：</p>
        <pre class="codeblock">mc alias set mystore ${esc(location.origin)} ${esc(created.accessKeyId)} &lt;secret&gt; --api S3v4 --path on
aws --endpoint-url ${esc(location.origin)} s3 ls</pre>`,
      footer: `<button class="btn btn--primary" data-act="close-modal">我已保存</button>`,
    });
  }

  /** 生成 48 位十六进制随机 Secret（浏览器 CSPRNG）。 */
  function generateSecret() {
    const bytes = new Uint8Array(24);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  }

  function openRotateKey(keyId, name) {
    const secret = generateSecret();
    openModal({
      title: '轮换 SecretAccessKey',
      wide: true,
      body: `
        <div class="notice">轮换后旧 Secret 立即失效，AccessKeyId 与桶属主保持不变，客户端只需替换 Secret 即可。</div>
        <div class="field">
          <label>AccessKeyId</label>
          <div class="secret-box"><span>${esc(keyId)}</span></div>
        </div>
        <div class="field">
          <label for="rotate-secret">新的 SecretAccessKey（可自行修改）</label>
          <input id="rotate-secret" type="text" value="${esc(secret)}" autocomplete="off" spellcheck="false">
        </div>
        <p class="login__hint" style="margin:0">对象 <strong>${esc(name)}</strong> 使用该密钥签名的会话也会被立即登出。</p>`,
      footer: `
        <button class="btn" data-act="close-modal">取消</button>
        <button class="btn btn--primary" data-act="submit-rotate-key" data-id="${esc(keyId)}">确认轮换</button>`,
      onMount: (root) => {
        $('[data-act="submit-rotate-key"]', root).addEventListener('click', async (event) => {
          const button = event.currentTarget;
          const value = byId('rotate-secret').value.trim();
          if (value.length < 16) {
            toast('Secret 至少需要 16 位', 'error');
            return;
          }

          button.disabled = true;
          try {
            await api(`/access-keys/${encodeURIComponent(button.dataset.id)}`, jsonRequest('PATCH', { secretAccessKey: value }));
            toast('Secret 已轮换');
            showSecret({ accessKeyId: keyId, secretAccessKey: value }, 'Secret 轮换成功');
            render();
          } catch (error) {
            button.disabled = false;
            toast(error.message, 'error');
          }
        });
      },
    });
  }

  // ---------------------------------------------------------- 上传

  function enqueueUploads(files, bucket, prefix) {
    if (!files || !files.length) return;

    const items = Array.from(files).map((file) => ({
      id: Math.random().toString(36).slice(2),
      file,
      key: prefix + file.name,
      pct: 0,
      state: 'waiting',
      node: null,
    }));

    state.uploads.push(...items);
    renderUploadsPanel();
    processUploads(bucket);
  }

  let uploadRunning = false;

  async function processUploads(bucket) {
    if (uploadRunning) return;
    uploadRunning = true;

    try {
      for (;;) {
        const item = state.uploads.find((entry) => entry.state === 'waiting');
        if (!item) break;

        item.state = 'uploading';
        paintUploadItem(item);

        await uploadOne(bucket, item);
        paintUploadItem(item);
      }
    } finally {
      uploadRunning = false;
    }

    const current = state.route;
    if (current.name === 'bucket' && current.bucket === bucket) {
      render();
    }
  }

  function uploadOne(bucket, item) {
    return new Promise((resolve) => {
      const xhr = new XMLHttpRequest();
      xhr.open('PUT', `/admin/api/buckets/${encBucket(bucket)}/objects/${encPath(item.key)}`);
      xhr.withCredentials = true;

      xhr.upload.onprogress = (event) => {
        if (!event.lengthComputable) return;
        item.pct = Math.round((event.loaded / event.total) * 100);
        paintUploadItem(item);
      };

      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          item.state = 'done';
          item.pct = 100;
        } else {
          item.state = 'error';
          item.error = parseError(xhr);
        }
        resolve();
      };

      xhr.onerror = () => {
        item.state = 'error';
        item.error = '网络错误';
        resolve();
      };

      xhr.setRequestHeader('Content-Type', item.file.type || 'application/octet-stream');
      xhr.send(item.file);
    });
  }

  function parseError(xhr) {
    try {
      const payload = JSON.parse(xhr.responseText);
      if (payload && payload.error) return payload.error;
    } catch { /* ignore */ }
    return `HTTP ${xhr.status}`;
  }

  function renderUploadsPanel() {
    const panel = byId('uploads');
    if (!state.uploads.length) {
      panel.hidden = true;
      panel.innerHTML = '';
      return;
    }

    panel.hidden = false;
    panel.innerHTML = `
      <div class="uploads__head">
        <span id="uploads-title"></span>
        <button class="link-btn" data-act="clear-uploads">清空</button>
      </div>
      <div class="uploads__list">
        ${state.uploads.map((item) => `
          <div class="upload-item" data-uid="${item.id}">
            <div class="upload-item__row">
              <span class="upload-item__name" title="${esc(item.key)}">${esc(item.key)}</span>
              <span class="upload-item__pct">0%</span>
            </div>
            <div class="bar"><i></i></div>
          </div>`).join('')}
      </div>`;

    state.uploads.forEach((item) => {
      item.node = panel.querySelector(`[data-uid="${item.id}"]`);
      paintUploadItem(item);
    });
  }

  function paintUploadItem(item) {
    if (!item.node) return;
    const bar = $('.bar > i', item.node);
    const pct = $('.upload-item__pct', item.node);

    if (bar) bar.style.width = `${item.state === 'waiting' ? 0 : item.pct}%`;
    if (pct) {
      if (item.state === 'error') pct.textContent = item.error || '失败';
      else if (item.state === 'done') pct.textContent = '完成';
      else pct.textContent = `${item.pct}%`;
    }

    item.node.classList.toggle('is-done', item.state === 'done');
    item.node.classList.toggle('is-error', item.state === 'error');

    const title = byId('uploads-title');
    if (title) {
      const done = state.uploads.filter((entry) => entry.state === 'done').length;
      const failed = state.uploads.filter((entry) => entry.state === 'error').length;
      title.textContent = `上传 ${done}/${state.uploads.length}${failed ? ` · 失败 ${failed}` : ''}`;
    }
  }

  function clearUploads() {
    state.uploads = state.uploads.filter((item) => item.state === 'uploading' || item.state === 'waiting');
    renderUploadsPanel();
  }

  // ---------------------------------------------------------- 行为分发

  async function handleAction(action, node) {
    switch (action) {
      case 'refresh':
        render();
        break;

      case 'logout':
        try { await api('/auth/logout', { method: 'POST' }); } catch { /* ignore */ }
        location.hash = '';
        location.reload();
        break;

      case 'create-bucket':
        openCreateBucket();
        break;

      case 'open-bucket':
        navigate(`#/buckets/${encBucket(node.dataset.name)}`);
        break;

      case 'delete-bucket': {
        const name = node.dataset.name;
        const count = Number(node.dataset.count || 0);
        openModal({
          title: `删除存储桶 ${name}`,
          body: `
            <p style="margin:0;color:var(--text-2);line-height:1.7">
              将永久删除存储桶 <code>${esc(name)}</code>${count > 0 ? ` 及其中的 <strong>${fmtNumber(count)}</strong> 个对象` : ''}，此操作不可恢复。
            </p>
            ${count > 0 ? `
            <label class="check-row">
              <input type="checkbox" id="force-delete">
              <span>我确认清空桶内 ${fmtNumber(count)} 个对象并删除存储桶</span>
            </label>` : ''}`,
          footer: `
            <button class="btn" data-act="close-modal">取消</button>
            <button class="btn btn--danger" data-act="confirm-delete-bucket" data-name="${esc(name)}" data-count="${count}"
              ${count > 0 ? 'disabled' : ''}>确认删除</button>`,
          onMount: (root) => {
            const force = byId('force-delete');
            const confirm = $('[data-act="confirm-delete-bucket"]', root);
            if (force && confirm) {
              force.addEventListener('change', () => { confirm.disabled = !force.checked; });
            }

            confirm.addEventListener('click', async () => {
              confirm.disabled = true;
              try {
                await api(`/buckets/${encBucket(name)}?force=true`, { method: 'DELETE' });
                closeModal();
                toast(`存储桶 ${name} 已删除`);
                navigate('#/buckets');
              } catch (error) {
                confirm.disabled = false;
                toast(error.message, 'error');
              }
            });
          },
        });
        break;
      }

      case 'goto-prefix':
        navigate(`#/buckets/${encBucket(state.route.bucket)}?prefix=${encodeURIComponent(node.dataset.prefix || '')}`);
        break;

      case 'upload':
        byId('file-input').click();
        break;

      case 'refresh-listing':
        render();
        break;

      case 'delete-object': {
        const key = node.dataset.key;
        confirmModal({
          title: '删除对象',
          message: `确定删除 ${key} 吗？此操作不可恢复。`,
          onConfirm: async () => {
            await api(`/buckets/${encBucket(state.route.bucket)}/objects/${encPath(key)}`, { method: 'DELETE' });
            toast('对象已删除');
            render();
          },
        });
        break;
      }

      case 'delete-selected': {
        const keys = Array.from(state.selection);
        if (!keys.length) return;
        confirmModal({
          title: `删除 ${keys.length} 个对象`,
          message: `将永久删除选中的 ${keys.length} 个对象，此操作不可恢复。`,
          onConfirm: async () => {
            const result = await api(
              `/buckets/${encBucket(state.route.bucket)}/objects/delete`,
              jsonRequest('POST', { keys }),
            );
            toast(`已删除 ${result.deleted} 个对象`);
            state.selection.clear();
            render();
          },
        });
        break;
      }

      case 'load-more': {
        const listing = state.listing;
        if (!listing || !listing.nextContinuationToken) return;
        node.disabled = true;
        node.textContent = '加载中…';
        try {
          const query = new URLSearchParams({
            prefix: listing.prefix,
            delimiter: listing.delimiter,
            maxKeys: '200',
            continuation: listing.nextContinuationToken,
          });
          const page = await api(`/buckets/${encBucket(listing.bucket)}/objects?${query.toString()}`);
          state.listing = {
            ...listing,
            objects: listing.objects.concat(page.objects || []),
            commonPrefixes: listing.commonPrefixes.concat(page.commonPrefixes || []),
            isTruncated: page.isTruncated,
            nextContinuationToken: page.nextContinuationToken,
          };
          byId('obj-table').innerHTML = objectTable(state.listing);
          applyFilter(byId('obj-filter') ? byId('obj-filter').value : '');
        } catch (error) {
          toast(error.message, 'error');
          node.disabled = false;
          node.textContent = '加载更多';
        }
        break;
      }

      case 'abort-upload':
        confirmModal({
          title: '终止分片上传',
          message: '将删除已上传的临时分片，未完成的对象不会出现在桶里。',
          confirmText: '终止上传',
          onConfirm: async () => {
            await api(`/buckets/${encBucket(state.route.bucket)}/uploads/${encodeURIComponent(node.dataset.id)}`, { method: 'DELETE' });
            toast('分片上传已终止');
            render();
          },
        });
        break;

      case 'create-key':
        openCreateKey();
        break;

      case 'toggle-key': {
        const enabled = node.dataset.enabled === 'true';
        node.disabled = true;
        try {
          await api(`/access-keys/${encodeURIComponent(node.dataset.id)}`, jsonRequest('PATCH', { enabled: !enabled }));
          toast(enabled ? '密钥已禁用' : '密钥已启用');
          render();
        } catch (error) {
          node.disabled = false;
          toast(error.message, 'error');
        }
        break;
      }

      case 'rotate-key':
        openRotateKey(node.dataset.id, node.dataset.name);
        break;

      case 'delete-key':
        confirmModal({
          title: '删除访问密钥',
          message: `删除后使用 ${node.dataset.name} 的客户端将立即无法访问，确定继续吗？`,
          onConfirm: async () => {
            await api(`/access-keys/${encodeURIComponent(node.dataset.id)}`, { method: 'DELETE' });
            toast('访问密钥已删除');
            render();
          },
        });
        break;

      case 'copy':
        try {
          await navigator.clipboard.writeText(node.dataset.copy);
          toast('已复制到剪贴板');
        } catch {
          toast('复制失败，请手动选择文本', 'error');
        }
        break;

      case 'clear-uploads':
        clearUploads();
        break;

      case 'close-modal':
        closeModal();
        break;

      default:
        break;
    }
  }

  // ---------------------------------------------------------- 事件绑定

  function applyFilter(keyword) {
    const needle = String(keyword || '').trim().toLowerCase();
    document.querySelectorAll('#obj-table tbody tr').forEach((row) => {
      if (!needle) {
        row.hidden = false;
        return;
      }
      row.hidden = !row.textContent.toLowerCase().includes(needle);
    });
  }

  function updateSelectionUi() {
    const button = byId('delete-selected');
    if (!button) return;
    const count = state.selection.size;
    button.disabled = count === 0;
    button.innerHTML = `${icon('trash')}删除选中${count ? ` (${count})` : ''}`;
  }

  function bindGlobalEvents() {
    document.addEventListener('click', (event) => {
      const node = event.target.closest('[data-act]');
      if (!node) return;

      // 行上的 data-act 与内部按钮的 data-act 冲突时，以最内层为准。
      const action = node.dataset.act;
      if (action === 'close-modal') {
        if (event.target.closest('.modal') && !event.target.closest('[data-act="close-modal"]')) return;
      }

      event.preventDefault();
      handleAction(action, node);
    });

    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') closeModal();
    });

    byId('view').addEventListener('change', (event) => {
      const target = event.target;

      if (target.id === 'file-input') {
        if (state.route.name !== 'bucket') return;
        enqueueUploads(target.files, state.route.bucket, state.listing?.prefix || '');
        target.value = '';
        return;
      }

      if (target.matches('[data-select]')) {
        const key = target.dataset.select;
        if (target.checked) state.selection.add(key);
        else state.selection.delete(key);
        updateSelectionUi();
      }
    });

    byId('view').addEventListener('input', (event) => {
      if (event.target.id === 'obj-filter') {
        applyFilter(event.target.value);
      }
    });

    // 拖拽上传
    byId('view').addEventListener('dragover', (event) => {
      const zone = event.target.closest('#dropzone');
      if (!zone) return;
      event.preventDefault();
      zone.classList.add('is-over');
      if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
    });

    byId('view').addEventListener('dragleave', (event) => {
      const zone = event.target.closest('#dropzone');
      if (!zone) return;
      if (zone.contains(event.relatedTarget)) return;
      zone.classList.remove('is-over');
    });

    byId('view').addEventListener('drop', (event) => {
      const zone = event.target.closest('#dropzone');
      if (!zone) return;
      event.preventDefault();
      zone.classList.remove('is-over');
      if (state.route.name !== 'bucket') return;
      enqueueUploads(event.dataTransfer?.files, state.route.bucket, state.listing?.prefix || '');
    });

    window.addEventListener('hashchange', applyRoute);
  }

  // ---------------------------------------------------------- 启动

  async function init() {
    byId('login-endpoint').textContent = location.origin;
    bindLogin();
    bindGlobalEvents();

    try {
      const response = await fetch('/admin/api/auth/me', { credentials: 'same-origin' });
      if (response.ok) {
        boot(await response.json());
        return;
      }
    } catch { /* 服务不可达时退回登录页 */ }

    showLogin();
  }

  document.addEventListener('DOMContentLoaded', init);
})();
