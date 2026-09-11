/* Public order form. Persian by default, English on request. */

const T = {
  fa: {
    tagline: 'پرداخت اشتراک به‌جای شما',
    formTitle: 'ثبت سفارش',
    formIntro: 'فرم را پر کنید. پس از بررسی، از همان راه ارتباطی که می‌نویسید با شما تماس گرفته می‌شود و مبلغ و روش تسویه را هماهنگ می‌کنیم.',
    plan: 'اشتراک', months: 'مدت', email: 'ایمیل اکانت Claude',
    contactKind: 'راه ارتباطی', contact: 'آیدی یا شماره', country: 'کشور محل استفاده',
    noteLabel: 'توضیح (اختیاری)', optPhone: 'تلفن',
    eligibleText: 'تأیید می‌کنم اکانت من در کشوری استفاده می‌شود که Anthropic آن را پشتیبانی می‌کند و شرایط استفاده را پذیرفته‌ام.',
    passwordWarning: 'هرگز رمز اکانتتان را در این فرم یا در پیام ننویسید. برای پرداخت لازم نیست.',
    submit: 'ثبت سفارش', submitting: 'در حال ارسال…',
    doneTitle: 'سفارش ثبت شد', doneBody: 'این کد را نگه دارید. با همین کد می‌توانید وضعیت سفارش را ببینید.',
    again: 'ثبت سفارش دیگر',
    trackTitle: 'پیگیری سفارش', trackBtn: 'پیگیری',
    monthUnit: ['۱ ماه', '۳ ماه', '۶ ماه', '۱۲ ماه'],
    status: {
      New: 'ثبت شده', Confirmed: 'تأیید شده', AwaitingPayment: 'در انتظار تسویه',
      Paid: 'پرداخت شد', Done: 'انجام شد', Cancelled: 'لغو شد'
    },
    errors: {
      too_many: 'تعداد درخواست‌ها زیاد بود. کمی بعد دوباره تلاش کنید.',
      unknown_plan: 'این اشتراک در دسترس نیست.',
      bad_email: 'ایمیل درست وارد نشده.',
      bad_contact: 'راه ارتباطی درست وارد نشده.',
      eligibility_required: 'باید تیک تأیید را بزنید.',
      not_found: 'سفارشی با این کد پیدا نشد.',
      network: 'ارتباط برقرار نشد. اینترنت را بررسی کنید.',
      generic: 'ثبت نشد. دوباره تلاش کنید.'
    },
    updated: 'آخرین به‌روزرسانی', message: 'پیام'
  },
  en: {
    tagline: 'Subscription paid on your behalf',
    formTitle: 'Place an order',
    formIntro: 'Fill this in. We reply on the contact you give here and agree the amount and how you settle up.',
    plan: 'Plan', months: 'Duration', email: 'Claude account email',
    contactKind: 'Reach you on', contact: 'Handle or number', country: 'Country of use',
    noteLabel: 'Note (optional)', optPhone: 'Phone',
    eligibleText: 'I confirm my account is used in a country Anthropic supports and I accept their terms.',
    passwordWarning: 'Never put your account password in this form or in a message. It is not needed to pay.',
    submit: 'Place order', submitting: 'Sending…',
    doneTitle: 'Order received', doneBody: 'Keep this code. You can check the status with it any time.',
    again: 'Place another order',
    trackTitle: 'Track an order', trackBtn: 'Track',
    monthUnit: ['1 month', '3 months', '6 months', '12 months'],
    status: {
      New: 'Received', Confirmed: 'Confirmed', AwaitingPayment: 'Awaiting payment',
      Paid: 'Paid', Done: 'Done', Cancelled: 'Cancelled'
    },
    errors: {
      too_many: 'Too many requests. Try again a little later.',
      unknown_plan: 'That plan is not available.',
      bad_email: 'That email does not look right.',
      bad_contact: 'That contact does not look right.',
      eligibility_required: 'Please tick the confirmation.',
      not_found: 'No order with that code.',
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

  document.querySelectorAll('[data-t]').forEach((el) => {
    const key = el.getAttribute('data-t');
    if (t()[key]) el.textContent = t()[key];
  });

  const months = $('months');
  [...months.options].forEach((option, index) => {
    option.textContent = t().monthUnit[index];
  });

  renderPlans();
  try { localStorage.setItem('cw_lang', lang); } catch (e) { /* private mode */ }
}

function renderPlans() {
  const select = $('plan');
  const current = select.value;
  select.innerHTML = '';

  service.plans.forEach((plan) => {
    const option = document.createElement('option');
    option.value = plan.key;
    const label = lang === 'fa' && plan.labelFa ? plan.labelFa : plan.label;
    // Isolate the price so a Persian line does not reorder "$20 / month".
    const price = plan.priceHint ? `⁦${plan.priceHint}⁩` : '';
    option.textContent = price ? `${label} — ${price}` : label;
    select.appendChild(option);
  });

  if (current) select.value = current;
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

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

loadService().then(applyLanguage);
