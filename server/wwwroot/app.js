/* Public order form. Persian by default, English on request. */

const T = {
  fa: {
    tagline: 'پرداخت اشتراک به‌جای شما',
    formTitle: 'ثبت سفارش',
    formIntro: 'فرم را پر کنید. پس از بررسی، از همان راه ارتباطی که می‌نویسید با شما تماس گرفته می‌شود و مبلغ و روش تسویه را هماهنگ می‌کنیم.',
    plan: 'اشتراک', months: 'مدت', email: 'ایمیل اکانتی که اشتراک رویش فعال شود',
    fullName: 'نام و نام خانوادگی', service: 'سرویس',
    contactKind: 'راه ارتباطی', contact: 'آیدی یا شماره', country: 'کشور محل استفاده',
    noteLabel: 'توضیح (اختیاری)', optPhone: 'تلفن',
    eligibleText: 'تأیید می‌کنم اکانت من در کشوری استفاده می‌شود که {vendor} آن را پشتیبانی می‌کند و شرایط استفاده را پذیرفته‌ام.',
    passwordWarning: 'هرگز رمز اکانتتان را در این فرم یا در پیام ننویسید. برای پرداخت لازم نیست.',
    submit: 'ثبت سفارش', submitting: 'در حال ارسال…',
    doneTitle: 'سفارش ثبت شد', doneBody: 'این کد را نگه دارید. با همین کد می‌توانید وضعیت سفارش را ببینید.',
    again: 'ثبت سفارش دیگر',
    trackTitle: 'پیگیری سفارش', trackBtn: 'پیگیری',
    keyTitle: 'وضعیت کلید', keyBtn: 'بررسی',
    keyIntro: 'اگر کلید دارید، اینجا ببینید چند روز اعتبار برایش مانده.',
    priceTitle: 'قیمت‌ها',
    priceIntro: 'قیمت‌ها با نرخ روز حساب می‌شوند و تا زمان تسویه ممکن است کمی جابه‌جا شوند.',
    priceUpdated: 'به‌روز شده در',
    popular: 'پرطرفدار',
    variable: 'مبلغ دلخواه',
    perMonth: 'ماهانه', perYear: 'سالانه', perSeat: 'هر نفر',
    tomanUnit: 'تومان',
    quoteFor: 'برای این انتخاب',
    monthUnit: ['۱ ماه', '۳ ماه', '۶ ماه', '۱۲ ماه'],
    status: {
      New: 'ثبت شده', Confirmed: 'تأیید شده', AwaitingPayment: 'در انتظار تسویه',
      Paid: 'پرداخت شد', Done: 'انجام شد', Cancelled: 'لغو شد'
    },
    keyState: {
      Unused: 'هنوز فعال نشده', Active: 'فعال',
      Expired: 'تمام شده', Revoked: 'لغو شده'
    },
    keyDaysLeft: 'روز اعتبار مانده',
    keyNotStarted: 'از روزی که اولین بار فعالش کنید شروع می‌شود.',
    keyFor: { both: 'برای هر دو سرویس', claude: 'فقط کلاد', chatgpt: 'فقط چت‌جی‌پی‌تی' },
    errors: {
      too_many: 'تعداد درخواست‌ها زیاد بود. کمی بعد دوباره تلاش کنید.',
      unknown_plan: 'این اشتراک در دسترس نیست.',
      bad_email: 'ایمیل درست وارد نشده.',
      bad_name: 'نام و نام خانوادگی را وارد کنید.',
      bad_contact: 'راه ارتباطی درست وارد نشده.',
      eligibility_required: 'باید تیک تأیید را بزنید.',
      not_found: 'سفارشی با این کد پیدا نشد.',
      key_not_found: 'کلیدی با این کد پیدا نشد.',
      network: 'ارتباط برقرار نشد. اینترنت را بررسی کنید.',
      generic: 'ثبت نشد. دوباره تلاش کنید.'
    },
    updated: 'آخرین به‌روزرسانی', message: 'پیام'
  },
  en: {
    tagline: 'Subscription paid on your behalf',
    formTitle: 'Place an order',
    formIntro: 'Fill this in. We reply on the contact you give here and agree the amount and how you settle up.',
    plan: 'Plan', months: 'Duration', email: 'Email of the account to activate',
    contactKind: 'Reach you on', contact: 'Handle or number', country: 'Country of use',
    fullName: 'Full name', service: 'Service',
    noteLabel: 'Note (optional)', optPhone: 'Phone',
    eligibleText: 'I confirm my account is used in a country {vendor} supports and I accept their terms.',
    passwordWarning: 'Never put your account password in this form or in a message. It is not needed to pay.',
    submit: 'Place order', submitting: 'Sending…',
    doneTitle: 'Order received', doneBody: 'Keep this code. You can check the status with it any time.',
    again: 'Place another order',
    trackTitle: 'Track an order', trackBtn: 'Track',
    keyTitle: 'Key status', keyBtn: 'Check',
    keyIntro: 'If you have a key, see how many days are left on it.',
    priceTitle: 'Prices',
    priceIntro: 'Prices follow the day’s rate and can move a little before you settle up.',
    priceUpdated: 'Updated',
    popular: 'Popular',
    variable: 'Any amount',
    perMonth: 'per month', perYear: 'per year', perSeat: 'per seat',
    tomanUnit: 'toman',
    quoteFor: 'For this choice',
    monthUnit: ['1 month', '3 months', '6 months', '12 months'],
    status: {
      New: 'Received', Confirmed: 'Confirmed', AwaitingPayment: 'Awaiting payment',
      Paid: 'Paid', Done: 'Done', Cancelled: 'Cancelled'
    },
    keyState: {
      Unused: 'Not activated yet', Active: 'Active',
      Expired: 'Finished', Revoked: 'Cancelled'
    },
    keyDaysLeft: 'days left',
    keyNotStarted: 'It starts counting the first time you activate it.',
    keyFor: { both: 'For both services', claude: 'Claude only', chatgpt: 'ChatGPT only' },
    errors: {
      too_many: 'Too many requests. Try again a little later.',
      unknown_plan: 'That plan is not available.',
      bad_email: 'That email does not look right.',
      bad_name: 'Please put your full name in.',
      bad_contact: 'That contact does not look right.',
      eligibility_required: 'Please tick the confirmation.',
      not_found: 'No order with that code.',
      key_not_found: 'No key with that code.',
      network: 'Could not reach the server.',
      generic: 'It did not go through. Try again.'
    },
    updated: 'Last update', message: 'Message'
  }
};

let lang = localStorage.getItem('cw_lang') || 'fa';
let service = { plans: [] };

const $ = (id) => document.getElementById(id);
const t = () => T[lang];

function applyLanguage() {
  document.documentElement.lang = lang;
  document.documentElement.dir = lang === 'fa' ? 'rtl' : 'ltr';
  document.body.dir = lang === 'fa' ? 'rtl' : 'ltr';
  $('lang').textContent = lang === 'fa' ? 'English' : 'فارسی';

  // {vendor} is whoever actually runs the service being ordered, so the
  // eligibility line never asks about Anthropic on a ChatGPT order.
  const vendor = chosenService() === 'chatgpt' ? 'OpenAI' : 'Anthropic';

  document.querySelectorAll('[data-t]').forEach((el) => {
    const key = el.getAttribute('data-t');
    if (t()[key]) el.textContent = String(t()[key]).replace('{vendor}', vendor);
  });

  const months = $('months');
  [...months.options].forEach((option, index) => {
    option.textContent = t().monthUnit[index];
  });

  renderPlans();
  renderPriceCards();
  renderQuote();
  try { localStorage.setItem('cw_lang', lang); } catch (e) { /* private mode */ }
}

$('plan').addEventListener('change', syncMonthsToPlan);
$('months').addEventListener('change', renderQuote);

// Picking the service repaints the page as well as the plan list, so it is
// obvious at a glance which of the two you are ordering.
$('service').addEventListener('change', () => {
  document.body.dataset.service = chosenService();
  applyLanguage();
  syncMonthsToPlan();
});

function chosenService() {
  return $('service').value === 'chatgpt' ? 'chatgpt' : 'claude';
}

function planService(plan) {
  return plan.service === 'chatgpt' ? 'chatgpt' : 'claude';
}

function renderPlans() {
  const select = $('plan');
  const current = select.value;
  const wanted = chosenService();
  select.innerHTML = '';

  service.plans.filter((plan) => planService(plan) === wanted).forEach((plan) => {
    const option = document.createElement('option');
    option.value = plan.key;
    const label = lang === 'fa' && plan.labelFa ? plan.labelFa : plan.label;

    // The toman figure if there is one; never the dollar hint, which would
    // put the cost price on the page the moment the owner turned it off.
    const quoted = priceOf(plan.key);
    const price = quoted && quoted.toman > 0 ? toman(quoted.toman) : '';

    option.textContent = price ? `${label} — ${price}` : label;
    select.appendChild(option);
  });

  // Keep the chosen plan if it belongs to this service; otherwise the first.
  if (current && [...select.options].some((o) => o.value === current)) {
    select.value = current;
  }
}

// ------------------------------------------------------------------ prices

let prices = { plans: [] };

function toman(value) {
  return Number(value || 0).toLocaleString(lang === 'fa' ? 'fa-IR' : 'en-GB') + ' ' + t().tomanUnit;
}

function periodLabel(period) {
  return { month: t().perMonth, year: t().perYear, seat: t().perSeat }[period] || '';
}

function priceOf(key) {
  return (prices.plans || []).find((p) => p.key === key);
}

async function loadPrices() {
  try {
    const response = await fetch('/api/pricing');
    prices = await response.json();
  } catch (e) {
    prices = { plans: [] };
  }

  renderPriceCards();
  renderQuote();
}

function renderPriceCards() {
  const card = $('priceCard');

  // No rate means no honest number to show, so the whole card stays away
  // rather than showing zeros.
  const rows = (prices.plans || [])
    .filter((plan) => plan.toman > 0 && planService(plan) === chosenService());

  if (!prices.rateReady || rows.length === 0) {
    card.classList.add('hidden');
    return;
  }

  card.classList.remove('hidden');
  card.querySelector('h2').textContent =
    t().priceTitle + ' — ' + (chosenService() === 'chatgpt' ? 'ChatGPT' : 'Claude');

  $('planCards').innerHTML = rows.map((plan) => {
    const label = lang === 'fa' && plan.labelFa ? plan.labelFa : plan.label;
    const note = lang === 'fa' && plan.noteFa ? plan.noteFa : plan.note;

    return `
      <div class="plancard ${plan.popular ? 'pop' : ''}">
        ${plan.popular ? `<span class="tag">${t().popular}</span>` : ''}
        <div class="name">${escapeHtml(label)}</div>
        <div class="price">${toman(plan.toman)}</div>
        <div class="per">${periodLabel(plan.period)}</div>
        ${note ? `<div class="pnote">${escapeHtml(note)}</div>` : ''}
      </div>`;
  }).join('');

  $('priceUpdated').textContent = prices.updatedAt
    ? t().priceUpdated + ' ' + new Date(prices.updatedAt).toLocaleString(lang === 'fa' ? 'fa-IR' : 'en-GB')
    : '';
}

/**
 * The running total under the plan and duration fields. A customer should not
 * have to send the form to find out what they are agreeing to.
 */
function renderQuote() {
  const box = $('quote');
  const plan = priceOf($('plan').value);
  const months = parseInt($('months').value, 10) || 1;

  if (!plan || !prices.rateReady) {
    box.classList.add('hidden');
    return;
  }

  if (plan.variable || plan.toman <= 0) {
    box.innerHTML = `<span class="q">${t().variable}</span>`;
    box.classList.remove('hidden');
    return;
  }

  // A yearly plan is already a year; multiplying it by the month picker would
  // quote twelve years. The picker is pinned instead.
  const times = plan.period === 'year' ? 1 : months;

  box.innerHTML = `<span class="k">${t().quoteFor}</span>
                   <span class="q">${toman(plan.toman * times)}</span>`;
  box.classList.remove('hidden');
}

function syncMonthsToPlan() {
  const plan = priceOf($('plan').value)
    || (service.plans || []).find((p) => p.key === $('plan').value);

  const yearly = plan && plan.period === 'year';
  const months = $('months');

  if (yearly) {
    months.value = '12';
  }

  months.disabled = !!yearly;
  renderQuote();
}

async function loadService() {
  try {
    const response = await fetch('/api/service');
    service = await response.json();
  } catch (e) {
    service = { plans: [] };
  }

  if (service.businessName) {
    $('brand').textContent = service.businessName;
    $('footBrand').textContent = service.businessName;
    document.title = service.businessName;
  }

  if (service.notice) {
    $('notice').textContent = service.notice;
    $('notice').classList.remove('hidden');
  }

  renderPlans();
}

function showError(message) {
  const box = $('formError');
  box.textContent = message;
  box.classList.remove('hidden');
}

$('lang').addEventListener('click', () => {
  lang = lang === 'fa' ? 'en' : 'fa';
  applyLanguage();
});

$('orderForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  $('formError').classList.add('hidden');

  const button = $('submit');
  button.disabled = true;
  button.querySelector('span').textContent = t().submitting;

  const payload = {
    plan: $('plan').value,
    months: parseInt($('months').value, 10),
    email: $('email').value.trim(),
    contact: $('contact').value.trim(),
    fullName: $('fullName').value.trim(),
    service: $('service').value,
    contactKind: $('contactKind').value,
    country: $('country').value.trim(),
    note: $('note').value.trim(),
    eligible: $('eligible').checked,
    website: $('website').value
  };

  try {
    const response = await fetch('/api/orders', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    const data = await response.json();

    if (!response.ok) {
      showError(t().errors[data.error] || t().errors.generic);
      return;
    }

    $('doneCode').textContent = data.code;

    if (service.contactLine) {
      const link = service.contactUrl
        ? `<a href="${service.contactUrl}" target="_blank" rel="noopener">${service.contactLine}</a>`
        : service.contactLine;
      $('doneContact').innerHTML = `<p class="hint">${link}</p>`;
    }

    $('formCard').classList.add('hidden');
    $('doneCard').classList.remove('hidden');
    window.scrollTo({ top: 0, behavior: 'smooth' });
  } catch (e) {
    showError(t().errors.network);
  } finally {
    button.disabled = false;
    button.querySelector('span').textContent = t().submit;
  }
});

$('again').addEventListener('click', () => {
  $('orderForm').reset();
  $('doneCard').classList.add('hidden');
  $('formCard').classList.remove('hidden');
});

$('trackBtn').addEventListener('click', track);
$('trackCode').addEventListener('keydown', (event) => {
  if (event.key === 'Enter') track();
});

async function track() {
  const code = $('trackCode').value.trim().toUpperCase();
  const box = $('trackResult');

  if (!code) return;
  box.innerHTML = '';

  try {
    const response = await fetch('/api/orders/' + encodeURIComponent(code));
    const data = await response.json();

    if (!response.ok) {
      box.innerHTML = `<div class="banner bad">${t().errors[data.error] || t().errors.generic}</div>`;
      return;
    }

    const label = t().status[data.status] || data.status;
    const when = new Date(data.updatedAt).toLocaleString(lang === 'fa' ? 'fa-IR' : 'en-GB');
    const message = data.message
      ? `<div class="kv"><div class="k">${t().message}</div><div class="v">${escapeHtml(data.message)}</div></div>`
      : '';

    box.innerHTML = `
      <div class="card tight" style="margin:0">
        <div class="line1" style="display:flex;gap:10px;align-items:center">
          <span class="code">${escapeHtml(data.code)}</span>
          <span class="pill s-${data.status}"><span class="dot"></span>${label}</span>
        </div>
        <div class="kv" style="margin-top:10px"><div class="k">${escapeHtml(data.plan)}</div><div class="v">${data.months} × ${t().monthUnit[0].replace(/^[^ ]+ /, '')}</div></div>
        <div class="kv"><div class="k">${t().updated}</div><div class="v">${when}</div></div>
        ${message}
      </div>`;
  } catch (e) {
    box.innerHTML = `<div class="banner bad">${t().errors.network}</div>`;
  }
}

// --------------------------------------------------------------- key status

$('keyBtn').addEventListener('click', checkKey);
$('keyCode').addEventListener('keydown', (event) => {
  if (event.key === 'Enter') checkKey();
});

async function checkKey() {
  const code = $('keyCode').value.trim().toUpperCase();
  const box = $('keyResult');

  if (!code) return;
  box.innerHTML = '';

  let response;
  let data;

  try {
    response = await fetch('/api/keys/' + encodeURIComponent(code));
    data = await response.json();
  } catch (e) {
    box.innerHTML = `<div class="banner bad">${t().errors.network}</div>`;
    return;
  }

  if (!response.ok) {
    const key = data.error === 'not_found' ? 'key_not_found' : data.error;
    box.innerHTML = `<div class="banner bad">${t().errors[key] || t().errors.generic}</div>`;
    return;
  }

  const state = t().keyState[data.state] || data.state;
  const good = data.state === 'Active';

  const detail = data.state === 'Unused'
    ? t().keyNotStarted
    : good
      ? `${Number(data.daysLeft).toLocaleString(lang === 'fa' ? 'fa-IR' : 'en-GB')} ${t().keyDaysLeft}`
      : '';

  box.innerHTML = `
    <div class="card tight" style="margin:0">
      <div style="display:flex;gap:10px;align-items:center;flex-wrap:wrap">
        <span class="code">${escapeHtml(code)}</span>
        <span class="kpill ${escapeHtml(data.state)}">${state}</span>
        <span class="hint" style="margin-inline-start:auto">${t().keyFor[data.service] || ''}</span>
      </div>
      ${detail ? `<p class="hint" style="margin-top:10px">${detail}</p>` : ''}
    </div>`;
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

loadService()
  .then(applyLanguage)
  .then(loadPrices)
  .then(syncMonthsToPlan);
