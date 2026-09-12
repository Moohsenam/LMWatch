/* Owner panel: list, filter and move orders along. */

const $ = (id) => document.getElementById(id);

const STATUS = {
  New: 'ثبت شده',
  Confirmed: 'تأیید شده',
  AwaitingPayment: 'در انتظار تسویه',
  Paid: 'پرداخت شد',
  Done: 'انجام شد',
  Cancelled: 'لغو شد'
};

const CONTACT = { telegram: 'Telegram', whatsapp: 'WhatsApp', email: 'Email', phone: 'تلفن' };

let orders = [];
let config = { plans: [] };

// ------------------------------------------------------------------ helpers

async function api(path, options = {}) {
  const headers = Object.assign({ 'X-CW': '1' }, options.headers || {});
  if (options.body) headers['Content-Type'] = 'application/json';

  const response = await fetch(path, Object.assign({}, options, { headers }));

  if (response.status === 401) {
    showLogin();
    throw new Error('unauthorized');
  }

  return response;
}

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

function when(value) {
  if (!value) return '—';
  return new Date(value).toLocaleString('fa-IR', {
    year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
  });
}

function money(amount, currency) {
  if (amount === null || amount === undefined || amount === '') return '—';
  return `${amount} ${esc(currency || '')}`.trim();
}

// -------------------------------------------------------------------- login

function showLogin() {
  $('loginView').classList.remove('hidden');
  $('panelView').classList.add('hidden');
}

function showPanel() {
  $('loginView').classList.add('hidden');
  $('panelView').classList.remove('hidden');
  loadOrders();
  loadConfig();
}

async function signIn() {
  const box = $('loginError');
  box.classList.add('hidden');
  $('loginBtn').disabled = true;

  try {
    const response = await fetch('/api/admin/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CW': '1' },
      body: JSON.stringify({ password: $('password').value })
    });

    if (response.ok) {
      $('password').value = '';
      showPanel();
      return;
    }

    const data = await response.json().catch(() => ({}));
    box.textContent = data.error === 'too_many'
      ? 'تلاش زیاد بود. ۱۵ دقیقه صبر کنید.'
      : 'رمز درست نیست.';
    box.classList.remove('hidden');
  } catch (e) {
    box.textContent = 'ارتباط با سرور برقرار نشد.';
    box.classList.remove('hidden');
  } finally {
    $('loginBtn').disabled = false;
  }
}

$('loginBtn').addEventListener('click', signIn);
$('password').addEventListener('keydown', (e) => { if (e.key === 'Enter') signIn(); });

$('logout').addEventListener('click', async () => {
  await fetch('/api/admin/logout', { method: 'POST', headers: { 'X-CW': '1' } });
  showLogin();
});

// ------------------------------------------------------------------- orders

async function loadOrders() {
  const status = $('statusFilter').value;
  const query = $('search').value.trim();
  const url = `/api/admin/orders?take=200&status=${encodeURIComponent(status)}&q=${encodeURIComponent(query)}`;

  try {
    const response = await api(url);
    const data = await response.json();
    orders = data.items || [];
    renderTiles(data.summary);
    renderList();
  } catch (e) {
    /* showLogin already ran */
  }
}

function renderTiles(summary) {
  if (!summary) return;

  const tiles = [
    { k: 'باز', v: summary.open },
    { k: 'این ماه', v: summary.monthCount },
    { k: 'پرداختی این ماه', v: summary.monthCost ? summary.monthCost + ' USD' : '—' },
    { k: 'دریافتی این ماه', v: summary.monthCharged || '—' },
    { k: 'کل سفارش‌ها', v: summary.total }
  ];

  $('tiles').innerHTML = tiles.map((tile) =>
    `<div class="tile"><div class="k">${tile.k}</div><div class="v">${esc(tile.v)}</div></div>`).join('');
}

function renderList() {
  const list = $('orderList');

  if (orders.length === 0) {
    list.innerHTML = '';
    $('emptyList').classList.remove('hidden');
    return;
  }

  $('emptyList').classList.add('hidden');
  list.innerHTML = orders.map((order) => `
    <div class="item" data-id="${esc(order.id)}">
      <div class="grow">
        <div class="line1">
          <span class="code">${esc(order.code)}</span>
          <span class="pill s-${esc(order.status)}"><span class="dot"></span>${STATUS[order.status] || order.status}</span>
          <span>${esc(order.planLabel)} · ${order.months} ماه</span>
        </div>
        <div class="line2">${esc(order.fullName) ? esc(order.fullName) + ' · ' : ''}${esc(order.accountEmail)} · ${esc(order.contact)}</div>
      </div>
      <div class="when">${when(order.createdAt)}</div>
    </div>`).join('');

  list.querySelectorAll('.item').forEach((element) => {
    element.addEventListener('click', () => openDrawer(element.dataset.id));
  });
}

$('refresh').addEventListener('click', loadOrders);
$('statusFilter').addEventListener('change', loadOrders);
$('exportCsv').addEventListener('click', () => window.open('/api/admin/export.csv', '_blank'));

let searchTimer = null;
$('search').addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(loadOrders, 250);
});

// ------------------------------------------------------------------- drawer

function openDrawer(id) {
  const order = orders.find((o) => o.id === id);
  if (!order) return;

  const statusOptions = Object.keys(STATUS).map((key) =>
    `<option value="${key}" ${order.status === key ? 'selected' : ''}>${STATUS[key]}</option>`).join('');

  const timeline = (order.history || []).slice().reverse().map((event) => `
    <div class="ev">
      <div class="t">${when(event.at)}</div>
      <div>${STATUS[event.status] || event.status}${event.note ? ' · ' + esc(event.note) : ''}</div>
    </div>`).join('');

  $('drawer').innerHTML = `
    <header>
      <span class="code" style="font-size:18px">${esc(order.code)}</span>
      <span class="pill s-${esc(order.status)}"><span class="dot"></span>${STATUS[order.status] || order.status}</span>
      <div class="grow"></div>
      <button class="ghost" id="closeDrawer">بستن</button>
    </header>

    <div class="kv"><div class="k">نام مشتری</div><div class="v">${esc(order.fullName) || '—'}</div></div>
    <div class="kv"><div class="k">سرویس</div><div class="v">${order.service === 'chatgpt' ? 'چت‌جی‌پی‌تی' : 'کلاد'}</div></div>
    <div class="kv"><div class="k">اشتراک</div><div class="v">${esc(order.planLabel)} · ${order.months} ماه</div></div>
    <div class="kv"><div class="k">ایمیل اکانت</div><div class="v" dir="ltr">${esc(order.accountEmail)}</div></div>
    <div class="kv"><div class="k">راه ارتباطی</div><div class="v" dir="ltr">${esc(order.contact)} (${CONTACT[order.contactKind] || esc(order.contactKind)})</div></div>
    <div class="kv"><div class="k">کشور</div><div class="v">${esc(order.country) || '—'}</div></div>
    <div class="kv"><div class="k">تأیید شرایط</div><div class="v">${order.eligibilityConfirmed ? 'بله' : 'نه'}</div></div>
    <div class="kv"><div class="k">ثبت</div><div class="v">${when(order.createdAt)}</div></div>
    ${order.note ? `<div class="kv"><div class="k">توضیح مشتری</div><div class="v">${esc(order.note)}</div></div>` : ''}

    <div class="divider"></div>

    <label class="field"><span>وضعیت</span><select id="dStatus">${statusOptions}</select></label>

    <div class="row">
      <label class="field"><span>دریافتی از مشتری</span><input type="number" step="0.01" id="dCharged" value="${order.chargedAmount ?? ''}"></label>
      <label class="field"><span>واحد</span><input type="text" id="dChargedCur" value="${esc(order.chargedCurrency || '')}" placeholder="EUR"></label>
    </div>

    <div class="row">
      <label class="field"><span>پرداختی من به کلاد</span><input type="number" step="0.01" id="dCost" value="${order.costAmount ?? ''}"></label>
      <label class="field"><span>واحد</span><input type="text" id="dCostCur" value="${esc(order.costCurrency || 'USD')}"></label>
    </div>

    <label class="field"><span>شناسه تراکنش</span><input type="text" id="dRef" dir="ltr" value="${esc(order.paymentRef || '')}"></label>
    <label class="field"><span>تاریخ تمدید بعدی</span><input type="text" id="dRenews" dir="ltr" placeholder="2026-10-09" value="${order.renewsAt ? String(order.renewsAt).slice(0, 10) : ''}"></label>

    <label class="field">
      <span>پیامی که مشتری با کد پیگیری می‌بیند</span>
      <textarea id="dOwnerNote" maxlength="2000">${esc(order.ownerNote || '')}</textarea>
    </label>

    <label class="field"><span>یادداشت این تغییر (اختیاری، فقط برای خودت)</span><input type="text" id="dHistory"></label>

    <div id="dMsg" class="banner ok hidden"></div>

    <div style="display:flex;gap:8px;margin-top:6px">
      <button class="primary" id="dSave" style="flex:1">ذخیره</button>
      <button class="danger small" id="dDelete">حذف</button>
    </div>

    <div class="divider"></div>
    <h3>تاریخچه</h3>
    <div class="timeline">${timeline}</div>`;

  $('overlay').classList.remove('hidden');
  $('closeDrawer').addEventListener('click', closeDrawer);
  $('dSave').addEventListener('click', () => saveOrder(order.id));
  $('dDelete').addEventListener('click', () => deleteOrder(order.id));
}

function closeDrawer() {
  $('overlay').classList.add('hidden');
  $('drawer').innerHTML = '';
}

$('overlay').addEventListener('click', (event) => {
  if (event.target === $('overlay')) closeDrawer();
});

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !$('overlay').classList.contains('hidden')) closeDrawer();
});

function numberOrNull(id) {
  const raw = $(id).value.trim();
  if (raw === '') return null;
  const value = Number(raw);
  return Number.isFinite(value) ? value : null;
}

async function saveOrder(id) {
  const renews = $('dRenews').value.trim();

  const payload = {
    status: $('dStatus').value,
    ownerNote: $('dOwnerNote').value,
    chargedAmount: numberOrNull('dCharged'),
    chargedCurrency: $('dChargedCur').value,
    costAmount: numberOrNull('dCost'),
    costCurrency: $('dCostCur').value,
    paymentRef: $('dRef').value,
    renewsAt: renews ? new Date(renews + 'T00:00:00Z').toISOString() : null,
    historyNote: $('dHistory').value
  };

  const button = $('dSave');
  button.disabled = true;

  try {
    const response = await api('/api/admin/orders/' + encodeURIComponent(id), {
      method: 'PATCH',
      body: JSON.stringify(payload)
    });

    const box = $('dMsg');

    if (response.ok) {
      box.className = 'banner ok';
      box.textContent = 'ذخیره شد.';
      box.classList.remove('hidden');
      await loadOrders();
      const updated = orders.find((o) => o.id === id);
      if (updated) openDrawer(id);
    } else {
      box.className = 'banner bad';
      box.textContent = 'ذخیره نشد.';
      box.classList.remove('hidden');
    }
  } finally {
    button.disabled = false;
  }
}

async function deleteOrder(id) {
  if (!confirm('این سفارش برای همیشه حذف شود؟')) return;

  const response = await api('/api/admin/orders/' + encodeURIComponent(id), { method: 'DELETE' });
  if (response.ok) {
    closeDrawer();
    loadOrders();
  }
}

// ----------------------------------------------------------------- settings

document.querySelectorAll('.tabs button').forEach((button) => {
  button.addEventListener('click', () => {
    document.querySelectorAll('.tabs button').forEach((b) => b.classList.remove('on'));
    button.classList.add('on');
    const tab = button.dataset.tab;
    $('tab-orders').classList.toggle('hidden', tab !== 'orders');
    $('tab-keys').classList.toggle('hidden', tab !== 'keys');
    $('tab-pricing').classList.toggle('hidden', tab !== 'pricing');
    $('tab-settings').classList.toggle('hidden', tab !== 'settings');

    if (tab === 'pricing') loadPricing();
    if (tab === 'keys') loadKeys();
  });
});

async function loadConfig() {
  try {
    const response = await api('/api/admin/config');
    config = await response.json();
  } catch (e) {
    return;
  }

  $('cfgName').value = config.businessName || '';
  $('cfgContact').value = config.contactLine || '';
  $('cfgContactUrl').value = config.contactUrl || '';
  $('cfgNotice').value = config.notice || '';
  $('cfgRequireKey').checked = config.requireKey !== false;
  $('cfgTrialDays').value = config.trialDays ?? 3;
  $('brandName').textContent = config.businessName || 'پنل سفارش‌ها';
  renderPlanRows();
}

const PERIODS = ['month', 'year', 'seat', ''];

function renderPlanRows() {
  $('planRows').innerHTML = (config.plans || []).map((plan, index) => `
    <div class="plan-row" data-index="${index}">
      <input type="text" class="k" value="${esc(plan.key)}" placeholder="key" dir="ltr">
      <input type="text" value="${esc(plan.label)}" placeholder="Claude Pro" dir="ltr">
      <input type="text" value="${esc(plan.labelFa || '')}" placeholder="کلاد پرو">
      <input type="number" class="u" value="${plan.usdPrice || 0}" placeholder="20" dir="ltr" title="قیمت دلاری">
      <select class="per" title="دوره">
        ${PERIODS.map((p) => `<option value="${p}" ${plan.period === p ? 'selected' : ''}>${
          { month: 'ماه', year: 'سال', seat: 'هر نفر', '': 'بدون دوره' }[p]
        }</option>`).join('')}
      </select>
      <input type="checkbox" class="pop" ${plan.popular ? 'checked' : ''} title="برجسته">
      <input type="checkbox" class="en" ${plan.enabled ? 'checked' : ''} title="نمایش">
      <button class="ghost small" data-remove="${index}">حذف</button>
    </div>`).join('');

  $('planRows').querySelectorAll('[data-remove]').forEach((button) => {
    button.addEventListener('click', () => {
      config.plans.splice(Number(button.dataset.remove), 1);
      renderPlanRows();
    });
  });
}

$('addPlan').addEventListener('click', () => {
  config.plans = config.plans || [];
  config.plans.push({ key: '', label: '', labelFa: '', usdPrice: 0, period: 'month', enabled: true });
  renderPlanRows();
});

function collectPlans() {
  return [...$('planRows').querySelectorAll('.plan-row')].map((row) => {
    const inputs = row.querySelectorAll('input');
    const existing = (config.plans || [])[Number(row.dataset.index)] || {};
    return {
      key: inputs[0].value.trim(),
      label: inputs[1].value.trim(),
      labelFa: inputs[2].value.trim(),
      usdPrice: Number(row.querySelector('.u').value) || 0,
      period: row.querySelector('.per').value,
      popular: row.querySelector('.pop').checked,
      enabled: row.querySelector('.en').checked,
      priceHint: existing.priceHint || '',
      note: existing.note || '',
      noteFa: existing.noteFa || ''
    };
  }).filter((plan) => plan.key.length > 0);
}

$('saveSettings').addEventListener('click', async () => {
  const payload = {
    businessName: $('cfgName').value,
    contactLine: $('cfgContact').value,
    contactUrl: $('cfgContactUrl').value,
    notice: $('cfgNotice').value,
    requireKey: $('cfgRequireKey').checked,
    trialDays: Number($('cfgTrialDays').value) || 0,
    plans: collectPlans()
  };

  const response = await api('/api/admin/config', { method: 'PUT', body: JSON.stringify(payload) });
  const box = $('settingsMsg');
  box.className = response.ok ? 'banner ok' : 'banner bad';
  box.textContent = response.ok ? 'ذخیره شد.' : 'ذخیره نشد.';
  box.classList.remove('hidden');

  if (response.ok) loadConfig();
});

// -------------------------------------------------------------------- keys

const KEY_STATE = {
  Unused: 'استفاده نشده',
  Active: 'فعال',
  Expired: 'منقضی',
  Revoked: 'لغو شده'
};

const KEY_SERVICE = { both: 'هر دو', claude: 'کلاد', chatgpt: 'چت‌جی‌پی‌تی' };

let keySearchTimer = null;

async function loadKeys() {
  const params = new URLSearchParams();
  if ($('keyFilter').value) params.set('state', $('keyFilter').value);
  if ($('keySearch').value.trim()) params.set('q', $('keySearch').value.trim());

  let data;
  try {
    const response = await api('/api/admin/keys?' + params.toString());
    data = await response.json();
  } catch (e) {
    return;
  }

  const s = data.summary || {};
  $('keyTiles').innerHTML = [
    ['همه', s.total],
    ['فعال', s.active],
    ['استفاده نشده', s.unused],
    ['رو به پایان', s.endingSoon],
    ['منقضی', s.expired]
  ].map(([k, v]) => `<div class="tile"><div class="k">${k}</div><div class="v">${v ?? 0}</div></div>`).join('');

  const items = data.items || [];
  $('keyEmpty').classList.toggle('hidden', items.length > 0);

  $('keyList').innerHTML = items.map((key) => {
    const bits = [];
    if (key.customer) bits.push(esc(key.customer));
    if (key.deviceName) bits.push('دستگاه: ' + esc(key.deviceName));
    if (key.state === 'Active') bits.push(key.daysLeft + ' روز مانده');
    if (key.state === 'Unused') bits.push(key.days + ' روزه');
    if (key.orderCode) bits.push('سفارش ' + esc(key.orderCode));
    if (key.note) bits.push(esc(key.note));

    return `
      <div class="key-row" data-code="${esc(key.code)}">
        <span class="code">${esc(key.code)}</span>
        <span class="kpill ${key.state}">${KEY_STATE[key.state] || key.state}</span>
        <span class="kpill Unused">${KEY_SERVICE[key.service] || key.service}</span>
        <span class="who">${bits.join(' · ') || '—'}</span>
        <span class="acts">
          <button class="ghost small" data-copy="${esc(key.code)}">کپی</button>
          ${key.deviceId ? `<button class="secondary small" data-release="${esc(key.code)}">آزادسازی دستگاه</button>` : ''}
          <button class="secondary small" data-add="${esc(key.code)}">+۳۰ روز</button>
          ${key.state === 'Revoked'
            ? `<button class="secondary small" data-unrevoke="${esc(key.code)}">برگردان</button>`
            : `<button class="danger small" data-revoke="${esc(key.code)}">لغو</button>`}
        </span>
      </div>`;
  }).join('');

  bindKeyActions();
}

function bindKeyActions() {
  const patch = async (code, body) => {
    await api('/api/admin/keys/' + encodeURIComponent(code), {
      method: 'PATCH',
      body: JSON.stringify(body)
    });
    loadKeys();
  };

  $('keyList').querySelectorAll('[data-copy]').forEach((b) =>
    b.addEventListener('click', () => {
      navigator.clipboard?.writeText(b.dataset.copy);
      b.textContent = 'کپی شد';
      setTimeout(() => (b.textContent = 'کپی'), 1200);
    }));

  $('keyList').querySelectorAll('[data-release]').forEach((b) =>
    b.addEventListener('click', () => patch(b.dataset.release, { releaseDevice: true })));

  $('keyList').querySelectorAll('[data-add]').forEach((b) =>
    b.addEventListener('click', () => patch(b.dataset.add, { addDays: 30 })));

  $('keyList').querySelectorAll('[data-revoke]').forEach((b) =>
    b.addEventListener('click', () => patch(b.dataset.revoke, { revoked: true })));

  $('keyList').querySelectorAll('[data-unrevoke]').forEach((b) =>
    b.addEventListener('click', () => patch(b.dataset.unrevoke, { revoked: false })));
}

$('makeKeys').addEventListener('click', async () => {
  const response = await api('/api/admin/keys', {
    method: 'POST',
    body: JSON.stringify({
      count: Number($('kCount').value) || 1,
      days: Number($('kDays').value) || 30,
      service: $('kService').value,
      customer: $('kCustomer').value.trim(),
      note: $('kNote').value.trim(),
      orderCode: $('kOrder').value.trim()
    })
  });

  const data = await response.json().catch(() => ({}));
  const box = $('madeKeys');

  if (data.ok) {
    box.className = 'banner ok';
    // Shown all at once so a batch can be copied in one go.
    box.innerHTML = 'ساخته شد:<br><span class="code" dir="ltr">'
      + data.codes.map(esc).join('<br>') + '</span>';
    $('kCustomer').value = '';
    $('kNote').value = '';
    $('kOrder').value = '';
    loadKeys();
  } else {
    box.className = 'banner bad';
    box.textContent = 'ساخته نشد.';
  }

  box.classList.remove('hidden');
});

$('keyRefresh').addEventListener('click', loadKeys);
$('keyFilter').addEventListener('change', loadKeys);
$('keySearch').addEventListener('input', () => {
  clearTimeout(keySearchTimer);
  keySearchTimer = setTimeout(loadKeys, 300);
});
$('keyCsv').addEventListener('click', () => window.open('/api/admin/keys.csv', '_blank'));

// ----------------------------------------------------------------- pricing

// Starting points, not promises: these feeds come and go. Press "آزمایش" and
// keep whichever one answers with a sane number.
const RATE_PRESETS = [
  { name: 'priceto.day', url: 'https://api.priceto.day/v1/latest/irr/usd', path: 'price', unit: 'toman' },
  { name: 'brsapi (فهرست ارزها)', url: 'https://brsapi.ir/FreeTsetmcBourseApi/Api_Free_Gold_Currency_v2.json', path: 'currency[symbol=USD].price', unit: 'rial' },
  { name: 'baha24', url: 'https://baha24.com/api/v1/price', path: 'USD.sell', unit: 'rial' },
  { name: 'نوبیتکس (تتر)', url: 'https://api.nobitex.ir/market/stats?srcCurrency=usdt&dstCurrency=rls', path: 'stats.usdt-rls.latest', unit: 'rial' }
];

let pricing = {};

function toman(value) {
  return Number(value || 0).toLocaleString('fa-IR') + ' تومان';
}

async function loadPricing() {
  try {
    const response = await api('/api/admin/pricing');
    pricing = await response.json();
  } catch (e) {
    return;
  }

  $('rateMode').value = pricing.rateMode || 'auto';
  $('manualRate').value = pricing.manualRateToman || '';
  $('rateUrl').value = pricing.sourceUrl || '';
  $('ratePath').value = pricing.sourcePath || '';
  $('rateUnit').value = pricing.sourceUnit || 'toman';
  $('rateMinutes').value = pricing.refreshMinutes || 60;
  $('rateMin').value = pricing.minRateToman || 10000;
  $('rateMax').value = pricing.maxRateToman || 10000000;
  $('markup').value = pricing.markupPercent ?? 15;
  $('roundTo').value = pricing.roundToToman ?? 10000;
  $('showUsd').checked = !!pricing.showUsd;

  if ($('ratePreset').options.length <= 1) {
    RATE_PRESETS.forEach((preset, index) => {
      const option = document.createElement('option');
      option.value = String(index);
      option.textContent = preset.name;
      $('ratePreset').appendChild(option);
    });
  }

  renderRateState();
  renderPricePreview();
  toggleRateBoxes();
}

function renderRateState() {
  const box = $('rateState');

  if (!pricing.rateReady) {
    box.className = 'banner bad';
    box.textContent = pricing.lastRateError
      ? 'نرخی در دست نیست: ' + pricing.lastRateError
      : 'نرخی در دست نیست. تا وقتی نرخ نباشد، اپ قیمتی نشان نمی‌دهد.';
    return;
  }

  box.className = pricing.rateStale ? 'banner bad' : 'banner ok';
  box.textContent = 'نرخ کنونی ' + toman(pricing.rate)
    + (pricing.rateAt ? ' — ' + when(pricing.rateAt) : '')
    + (pricing.rateStale ? ' (قدیمی است)' : '');
}

function renderPricePreview() {
  const rows = (pricing.preview || []).filter((row) => row.usdPrice > 0);

  if (!rows.length || !pricing.rateReady) {
    $('pricePreview').innerHTML = '<p class="hint">اول نرخ را درست کنید تا پیش‌نمایش بیاید.</p>';
    return;
  }

  $('pricePreview').innerHTML = rows.map((row) => `
    <div class="kv"><span class="k">${esc(row.label)}</span>
      <span dir="ltr">$${row.usdPrice}</span>
      <strong style="margin-inline-start:auto">${toman(row.toman)}</strong>
    </div>`).join('');
}

function toggleRateBoxes() {
  const auto = $('rateMode').value === 'auto';
  $('autoBox').classList.toggle('hidden', !auto);
  $('manualBox').classList.toggle('hidden', auto);
}

$('rateMode').addEventListener('change', toggleRateBoxes);

$('ratePreset').addEventListener('change', () => {
  const preset = RATE_PRESETS[Number($('ratePreset').value)];
  if (!preset) return;
  $('rateUrl').value = preset.url;
  $('ratePath').value = preset.path;
  $('rateUnit').value = preset.unit;
});

$('testRate').addEventListener('click', async () => {
  const box = $('testMsg');
  box.className = 'banner';
  box.textContent = 'در حال آزمایش...';
  box.classList.remove('hidden');

  const response = await api('/api/admin/pricing/test', {
    method: 'POST',
    body: JSON.stringify({ url: $('rateUrl').value, path: $('ratePath').value, unit: $('rateUnit').value })
  });

  const data = await response.json().catch(() => ({}));

  if (data.ok) {
    box.className = 'banner ok';
    box.textContent = 'جواب داد: ' + toman(data.toman) + ' (عدد خام ' + data.raw + ')';
  } else {
    box.className = 'banner bad';
    box.textContent = 'جواب نداد: ' + (data.error || 'نامشخص');
  }
});

$('refreshRate').addEventListener('click', async () => {
  await api('/api/admin/pricing/refresh', { method: 'POST' });
  loadPricing();
});

$('savePricing').addEventListener('click', async () => {
  const payload = {
    pricing: {
      markupPercent: Number($('markup').value) || 0,
      roundToToman: Number($('roundTo').value) || 0,
      rateMode: $('rateMode').value,
      manualRateToman: Number($('manualRate').value) || 0,
      sourceUrl: $('rateUrl').value.trim(),
      sourcePath: $('ratePath').value.trim(),
      sourceUnit: $('rateUnit').value,
      refreshMinutes: Number($('rateMinutes').value) || 60,
      minRateToman: Number($('rateMin').value) || 10000,
      maxRateToman: Number($('rateMax').value) || 10000000,
      showUsd: $('showUsd').checked
    }
  };

  const response = await api('/api/admin/config', { method: 'PUT', body: JSON.stringify(payload) });
  const box = $('pricingMsg');
  box.className = response.ok ? 'banner ok' : 'banner bad';
  box.textContent = response.ok ? 'ذخیره شد.' : 'ذخیره نشد.';
  box.classList.remove('hidden');

  if (response.ok) loadPricing();
});

$('changePw').addEventListener('click', async () => {
  const box = $('pwMsg');
  box.classList.add('hidden');

  const response = await api('/api/admin/password', {
    method: 'POST',
    body: JSON.stringify({ current: $('pwCurrent').value, next: $('pwNext').value })
  });

  const data = await response.json().catch(() => ({}));

  if (response.ok) {
    box.className = 'banner ok';
    box.textContent = 'رمز عوض شد. دوباره وارد شوید.';
    box.classList.remove('hidden');
    setTimeout(showLogin, 1200);
    return;
  }

  box.className = 'banner bad';
  box.textContent = data.error === 'too_short' ? 'رمز جدید کوتاه است.' : 'رمز فعلی درست نیست.';
  box.classList.remove('hidden');
});

// ------------------------------------------------------------------- start

fetch('/api/admin/me')
  .then((r) => r.json())
  .then((data) => (data.signedIn ? showPanel() : showLogin()))
  .catch(showLogin);
