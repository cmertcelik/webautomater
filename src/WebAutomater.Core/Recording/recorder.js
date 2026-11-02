// WebAutomater kayıt betiği.
// Sayfadaki kullanıcı etkileşimlerini yakalar ve window.__waRecord bağlantısı
// üzerinden .NET tarafına JSON olarak iletir. Her sayfa ve çerçevede çalışır.
(() => {
  if (window.__waRecorderInstalled) return;
  window.__waRecorderInstalled = true;

  const NON_TEXT_INPUTS = new Set(['checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'image', 'hidden', 'range', 'color']);
  const TEST_ID_ATTRS = ['data-testid', 'data-test-id', 'data-test', 'data-qa', 'data-cy', 'data-automation-id'];
  const CLICKABLE = [
    'button', 'a', 'input', 'select', 'textarea', 'label', 'summary', 'option', '[onclick]',
    '[role=button]', '[role=link]', '[role=menuitem]', '[role=menuitemcheckbox]', '[role=menuitemradio]',
    '[role=tab]', '[role=option]', '[role=checkbox]', '[role=radio]', '[role=switch]', '[role=treeitem]',
  ].join(',');
  const NAMED_ROLES = new Set([
    'button', 'link', 'checkbox', 'radio', 'tab', 'menuitem', 'menuitemcheckbox', 'menuitemradio', 'option',
    'switch', 'treeitem', 'textbox', 'combobox', 'searchbox', 'spinbutton', 'listbox',
  ]);
  const ROLES_NAMED_BY_CONTENT = new Set([
    'button', 'link', 'tab', 'menuitem', 'menuitemcheckbox', 'menuitemradio', 'option', 'checkbox', 'radio',
    'switch', 'treeitem', 'heading', 'listitem', 'cell',
  ]);
  const RECORDED_KEYS = new Set(['Enter', 'Escape', 'Tab', 'ArrowDown', 'ArrowUp']);

  let mode = 'record';

  // Uygulamanın sayfaya eklediği kendi arayüzü (çubuk, panel, vurgu) kayda ya da kontrole girmez.
  function isOwnUi(e) {
    const path = e.composedPath ? e.composedPath() : [];
    return path.some(n => n.localName && n.localName.startsWith('wa-'));
  }

  function recording(e) {
    return mode === 'record' && !isOwnUi(e);
  }

  function send(payload) {
    payload.ts = Date.now();
    if (window.top === window) {
      payload.vw = window.innerWidth;
      payload.vh = window.innerHeight;
    }
    try {
      window.__waRecord(JSON.stringify(payload));
    } catch (e) {
      // Bağlantı henüz hazır değilse işlem kaybolur; sayfayı bozmamak için sessiz geçilir.
    }
  }

  function eventTarget(e) {
    const path = e.composedPath ? e.composedPath() : [];
    const t = path.length && path[0] instanceof Element ? path[0] : e.target;
    return t instanceof Element ? t : null;
  }

  function norm(s, max) {
    s = (s || '').replace(/\s+/g, ' ').trim();
    return max && s.length > max ? s.slice(0, max) + '…' : s;
  }

  function quote(s) {
    return '"' + s.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
  }

  function isTextInput(el) {
    if (el instanceof HTMLTextAreaElement) return true;
    if (el instanceof HTMLInputElement) return !NON_TEXT_INPUTS.has((el.type || 'text').toLowerCase());
    return !!el.isContentEditable;
  }

  function isFormField(el) {
    return el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement;
  }

  // Otomatik üretilmiş, her yüklemede değişebilecek kimlik/sınıf adlarını eler.
  function looksGenerated(v) {
    return !v || v.length > 50 || /^\d/.test(v) || /\d{4,}/.test(v) || /[0-9a-f]{8,}/i.test(v)
      || /^(ember|ext-|ng-|react-|mui-|:r|radix-|headlessui-|css-|sc-|jsx-)/i.test(v);
  }

  function implicitRole(el) {
    const explicit = (el.getAttribute('role') || '').trim().split(/\s+/)[0];
    if (explicit) return explicit;
    const tag = el.localName;
    if ((tag === 'a' || tag === 'area') && el.hasAttribute('href')) return 'link';
    if (tag === 'button') return 'button';
    if (tag === 'select') return el.multiple || el.size > 1 ? 'listbox' : 'combobox';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'option') return 'option';
    if (tag === 'li') return 'listitem';
    if (/^h[1-6]$/.test(tag)) return 'heading';
    if (tag === 'img' && el.getAttribute('alt')) return 'img';
    if (tag === 'input') {
      const type = (el.type || 'text').toLowerCase();
      if (['button', 'submit', 'reset', 'image'].includes(type)) return 'button';
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      if (type === 'range') return 'slider';
      if (type === 'number') return 'spinbutton';
      if (el.hasAttribute('list')) return 'combobox';
      if (type === 'search') return 'searchbox';
      if (['text', 'email', 'tel', 'url'].includes(type)) return 'textbox';
    }
    return '';
  }

  function labelText(el) {
    if (el.labels && el.labels.length) return norm(el.labels[0].textContent);
    return '';
  }

  function accessibleName(el) {
    const aria = el.getAttribute('aria-label');
    if (aria && aria.trim()) return norm(aria);
    const ids = el.getAttribute('aria-labelledby');
    if (ids) {
      const text = ids.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean)
        .map(x => norm(x.textContent)).join(' ').trim();
      if (text) return text;
    }
    const label = labelText(el);
    if (label) return label;
    if (el instanceof HTMLInputElement && ['button', 'submit', 'reset'].includes(el.type)) return norm(el.value);
    if (el instanceof HTMLImageElement) return norm(el.alt);
    if (ROLES_NAMED_BY_CONTENT.has(implicitRole(el))) {
      const text = norm(el.innerText || el.textContent);
      if (text) return text;
    }
    return norm(el.getAttribute('title'));
  }

  function humanName(el) {
    return accessibleName(el)
      || norm(el.getAttribute('placeholder'))
      || norm(el.getAttribute('name'))
      || norm(el.innerText || el.textContent, 40)
      || el.localName;
  }

  function kindOf(el) {
    if (el instanceof HTMLInputElement && el.type === 'password') return 'password';
    if (el instanceof HTMLInputElement && el.type === 'file') return 'file';
    return implicitRole(el) || (el.isContentEditable ? 'textbox' : el.localName);
  }

  function cssPath(el) {
    const parts = [];
    let cur = el;
    while (cur && cur.nodeType === 1 && cur !== document.documentElement) {
      if (cur !== el && cur.id && !looksGenerated(cur.id)) {
        parts.unshift('#' + CSS.escape(cur.id));
        break;
      }
      const tag = cur.localName;
      let seg = CSS.escape(tag);
      const parent = cur.parentElement;
      if (parent) {
        const same = Array.from(parent.children).filter(c => c.localName === tag);
        if (same.length > 1) seg += ':nth-of-type(' + (same.indexOf(cur) + 1) + ')';
      }
      parts.unshift(seg);
      cur = parent;
    }
    return parts.join(' > ');
  }

  // En okunaklı ve kararlı olandan en kırılgana doğru aday seçiciler üretir.
  // CSS adayları burada tekillik açısından denetlenir; rol/metin adayları oynatma sırasında denetlenir.
  function selectorsFor(el) {
    const out = [];
    const add = s => { if (s && !out.includes(s)) out.push(s); };
    const root = el.getRootNode();
    const unique = s => {
      try {
        const found = root.querySelectorAll(s);
        return found.length === 1 && found[0] === el;
      } catch (e) {
        return false;
      }
    };
    const tag = CSS.escape(el.localName);
    const attr = (name, value) => tag + '[' + name + '=' + quote(value) + ']';

    for (const a of TEST_ID_ATTRS) {
      const v = el.getAttribute(a);
      if (v && unique('[' + a + '=' + quote(v) + ']')) add('[' + a + '=' + quote(v) + ']');
    }
    if (el.id && !looksGenerated(el.id) && unique('#' + CSS.escape(el.id))) add('#' + CSS.escape(el.id));

    const name = el.getAttribute('name');
    if (name && (isFormField(el) || el.localName === 'button')) {
      let s = attr('name', name);
      if (el instanceof HTMLInputElement && (el.type === 'radio' || el.type === 'checkbox') && el.value)
        s += '[value=' + quote(el.value) + ']';
      if (unique(s)) add(s);
    }
    for (const a of ['aria-label', 'placeholder']) {
      const v = el.getAttribute(a);
      if (v && unique(attr(a, v))) add(attr(a, v));
    }

    const role = implicitRole(el);
    const accName = accessibleName(el);
    if (NAMED_ROLES.has(role) && accName && accName.length <= 80)
      add('internal:role=' + role + '[name=' + quote(accName) + 'i]');

    if (isFormField(el)) {
      const label = labelText(el);
      if (label && label.length <= 80) add('internal:label=' + quote(label) + 'i');
    } else {
      const text = norm(el.innerText);
      if (text && text.length <= 50) add('internal:text=' + quote(text) + 'i');
    }

    const classes = Array.from(el.classList)
      .filter(c => !looksGenerated(c) && !/^(active|focus|hover|selected|open|show|disabled|is-|has-)/.test(c))
      .slice(0, 3);
    if (classes.length) {
      const s = tag + classes.map(c => '.' + CSS.escape(c)).join('');
      if (unique(s)) add(s);
    }

    add(cssPath(el));
    return out.slice(0, 6);
  }

  function describe(el) {
    return { selectors: selectorsFor(el), name: humanName(el), kind: kindOf(el) };
  }

  function clickTarget(t) {
    const svg = t instanceof SVGElement ? t.closest('svg') : null;
    if (svg) t = svg;
    return t.closest(CLICKABLE) || t;
  }

  function togglesViaChange(el) {
    return el instanceof HTMLInputElement && ['checkbox', 'radio', 'file'].includes(el.type);
  }

  function shouldRecordClick(e, el) {
    if (!e.isTrusted) return false;
    // detail === 0: klavyeyle tetiklenen tıklama; tuş basımı ayrıca kaydedilir.
    if (e.detail === 0) return false;
    if (el instanceof HTMLSelectElement || el instanceof HTMLOptionElement) return false;
    // Onay kutusu/radyo/dosya seçimleri "change" olayından kaydedilir; etiket tıklaması da onu tetikler.
    if (togglesViaChange(el)) return false;
    const label = el.closest('label');
    if (label && label.control && togglesViaChange(label.control)) return false;
    return true;
  }

  window.addEventListener('click', e => {
    if (!recording(e)) return;
    const t = eventTarget(e);
    if (!t) return;
    const el = clickTarget(t);
    if (shouldRecordClick(e, el)) send({ type: 'click', ...describe(el) });
  }, true);

  window.addEventListener('dblclick', e => {
    if (!recording(e)) return;
    const t = eventTarget(e);
    if (!t || !e.isTrusted) return;
    const el = clickTarget(t);
    if (shouldRecordClick(e, el)) send({ type: 'dblclick', ...describe(el) });
  }, true);

  window.addEventListener('input', e => {
    if (!recording(e)) return;
    const t = eventTarget(e);
    if (!t || !isTextInput(t)) return;
    const isNative = t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement;
    send({ type: 'fill', value: isNative ? t.value : t.innerText, ...describe(t) });
  }, true);

  window.addEventListener('change', e => {
    if (!recording(e)) return;
    const t = eventTarget(e);
    if (!t) return;
    if (t instanceof HTMLSelectElement) {
      const opts = Array.from(t.selectedOptions);
      send({
        type: 'select',
        value: opts.map(o => o.value).join('|'),
        display: opts.map(o => norm(o.label || o.textContent)).join(', '),
        multiple: t.multiple,
        ...describe(t),
      });
    } else if (t instanceof HTMLInputElement && (t.type === 'checkbox' || t.type === 'radio')) {
      send({ type: t.checked ? 'check' : 'uncheck', ...describe(t) });
    } else if (t instanceof HTMLInputElement && t.type === 'file') {
      send({ type: 'upload', value: Array.from(t.files || []).map(f => f.name).join('|'), ...describe(t) });
    }
  }, true);

  window.addEventListener('keydown', e => {
    if (!recording(e)) return;
    if (!e.isTrusted || e.ctrlKey || e.metaKey || e.altKey) return;
    const t = eventTarget(e);
    if (!t || t instanceof HTMLSelectElement) return;
    let key = e.key;
    if (key === ' ') {
      if (isTextInput(t) || togglesViaChange(t)) return;
      key = 'Space';
    } else if (!RECORDED_KEYS.has(key)) {
      return;
    }
    // Çok satırlı alanlarda Enter yeni satırdır ve zaten yazılan metnin parçasıdır.
    if (key === 'Enter' && (t instanceof HTMLTextAreaElement || t.isContentEditable)) return;
    if ((key === 'ArrowDown' || key === 'ArrowUp') && !isTextInput(t)) return;
    if (key === 'Tab' && e.shiftKey) key = 'Shift+Tab';
    send({ type: 'press', value: key, ...describe(t) });
  }, true);

  // ---------- Kontrol modu ----------
  // Bu moddayken sayfaya yapılan tıklamalar siteye ulaşmaz; tıklanan öğenin özellikleri okunur
  // ve kullanıcı hangi kontrolü istediğini seçer.

  const BLOCKED_EVENTS = ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'click', 'dblclick', 'auxclick', 'contextmenu'];
  let highlight = null;
  let panel = null;

  function elementAt(x, y) {
    let el = document.elementFromPoint(x, y);
    while (el && el.shadowRoot) {
      const inner = el.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === el) break;
      el = inner;
    }
    return el;
  }

  // Tablo hücresi gibi tıklanabilir olmayan öğeler olduğu gibi alınır, butonun içindeki ikon butona yükseltilir.
  const inspectTarget = el => clickTarget(el);

  function showHighlight(el) {
    if (!highlight) {
      highlight = document.createElement('wa-check-highlight');
      highlight.setAttribute('style', 'position:fixed;z-index:2147483646;pointer-events:none;outline:2px solid #f9a825;background:rgba(249,168,37,.18);border-radius:2px');
      (document.body || document.documentElement).appendChild(highlight);
    }
    const r = el.getBoundingClientRect();
    highlight.style.left = r.left + 'px';
    highlight.style.top = r.top + 'px';
    highlight.style.width = r.width + 'px';
    highlight.style.height = r.height + 'px';
    highlight.style.display = 'block';
  }

  function hideHighlight() {
    if (highlight) highlight.style.display = 'none';
  }

  function closePanel() {
    if (panel) panel.remove();
    panel = null;
  }

  function readInfo(el) {
    const isField = isFormField(el);
    const type = el instanceof HTMLInputElement ? (el.type || 'text').toLowerCase() : '';
    const checkable = type === 'checkbox' || type === 'radio' || ['checkbox', 'radio', 'switch'].includes(el.getAttribute('role'));
    const checked = el.checked === true || el.getAttribute('aria-checked') === 'true';
    const interactive = isField || el.matches(CLICKABLE);
    const disabled = el.disabled === true || el.getAttribute('aria-disabled') === 'true';
    let text = '';
    if (type !== 'password') {
      if (el instanceof HTMLSelectElement) text = Array.from(el.selectedOptions).map(o => norm(o.label || o.textContent)).join(', ');
      else if (isField && !checkable) text = el.value || '';
      else text = norm(el.innerText || el.textContent);
    }
    return { checkable, checked, interactive, disabled, text: norm(text, 300) };
  }

  function conditionChoices(info) {
    const choices = [];
    if (info.checkable) {
      choices.push({ id: 'checked', label: 'İşaretli mi' + (info.checked ? ' (şu an işaretli)' : '') });
      choices.push({ id: 'unchecked', label: 'İşaretsiz mi' + (info.checked ? '' : ' (şu an işaretsiz)') });
    }
    if (info.interactive) {
      choices.push({ id: 'enabled', label: 'Aktif mi' + (info.disabled ? '' : ' (şu an aktif)') });
      choices.push({ id: 'disabled', label: 'Pasif mi' + (info.disabled ? ' (şu an pasif)' : '') });
    }
    if (info.text) {
      choices.push({ id: 'textEquals', label: 'Metni şuna eşit mi', text: true });
      choices.push({ id: 'textContains', label: 'Metni şunu içeriyor mu', text: true });
      choices.push({ id: 'pageHasText', label: 'Bu yazı sayfanın herhangi bir yerinde var mı', text: true });
    }
    choices.push({ id: 'visible', label: 'Görünüyor mu' });
    return choices;
  }

  function defaultChoice(info) {
    if (info.checkable) return info.checked ? 'checked' : 'unchecked';
    if (info.interactive) return info.disabled ? 'disabled' : 'enabled';
    return info.text ? 'textEquals' : 'visible';
  }

  function openPanel(el, x, y) {
    closePanel();
    const info = readInfo(el);
    const d = describe(el);
    panel = document.createElement('wa-check-panel');
    const left = Math.max(8, Math.min(x + 12, window.innerWidth - 340));
    const top = Math.max(8, Math.min(y + 12, window.innerHeight - 330));
    panel.setAttribute('style', 'position:fixed;z-index:2147483647;left:' + left + 'px;top:' + top + 'px');
    const root = panel.attachShadow({ mode: 'open' });

    const style = document.createElement('style');
    style.textContent = [
      '.box{width:320px;background:#fff;color:#222;font:13px system-ui,sans-serif;border-radius:8px;box-shadow:0 6px 24px rgba(0,0,0,.35);padding:12px}',
      '.title{font-weight:600;margin-bottom:2px;word-break:break-word}',
      '.sub{color:#666;font-size:12px;margin-bottom:8px}',
      'label{display:block;padding:3px 0;cursor:pointer}',
      'input[type=text]{width:100%;box-sizing:border-box;padding:5px 6px;margin:6px 0;border:1px solid #bbb;border-radius:4px;font:inherit}',
      '.row{display:flex;gap:6px;justify-content:flex-end;margin-top:8px}',
      'button{font:inherit;padding:5px 12px;border-radius:4px;border:1px solid #bbb;background:#f4f4f4;cursor:pointer}',
      'button.ok{background:#f9a825;border-color:#f9a825;font-weight:600}',
    ].join('');
    root.appendChild(style);

    const box = document.createElement('div');
    box.className = 'box';
    const title = document.createElement('div');
    title.className = 'title';
    title.textContent = 'Kontrol: ' + norm(d.name, 60);
    const sub = document.createElement('div');
    sub.className = 'sub';
    sub.textContent = '<' + el.localName + '>' + (info.interactive ? (info.disabled ? ' · şu an pasif' : ' · şu an aktif') : '');
    box.append(title, sub);

    const textInput = document.createElement('input');
    textInput.type = 'text';
    textInput.value = info.text;
    textInput.placeholder = 'Aranacak metin';
    const choices = conditionChoices(info);
    let selected = defaultChoice(info);
    if (!choices.some(c => c.id === selected)) selected = choices[0].id;
    const syncText = () => {
      const c = choices.find(x => x.id === selected);
      textInput.style.display = c && c.text ? 'block' : 'none';
    };
    for (const c of choices) {
      const label = document.createElement('label');
      const radio = document.createElement('input');
      radio.type = 'radio';
      radio.name = 'kind';
      radio.value = c.id;
      radio.checked = c.id === selected;
      radio.addEventListener('change', () => { selected = c.id; syncText(); });
      label.append(radio, ' ' + c.label);
      box.appendChild(label);
    }
    box.appendChild(textInput);
    syncText();

    const row = document.createElement('div');
    row.className = 'row';
    const cancel = document.createElement('button');
    cancel.textContent = 'İptal';
    cancel.dataset.action = 'cancel';
    cancel.addEventListener('click', closePanel);
    const ok = document.createElement('button');
    ok.className = 'ok';
    ok.textContent = 'Kontrolü ekle';
    ok.dataset.action = 'add';
    ok.addEventListener('click', () => {
      const c = choices.find(x => x.id === selected);
      send({ type: 'condition', condition: selected, value: c && c.text ? textInput.value : null, selectors: d.selectors, name: d.name, kind: d.kind });
      closePanel();
      changeMode('record');
    });
    row.append(cancel, ok);
    box.appendChild(row);
    root.appendChild(box);
    (document.body || document.documentElement).appendChild(panel);
  }

  for (const type of BLOCKED_EVENTS) {
    window.addEventListener(type, e => {
      if (mode !== 'check' || isOwnUi(e)) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      // Pasif butonlar click üretmez ama pointerdown üretir; seçim bu yüzden burada yapılır.
      if (type !== 'pointerdown' || e.button !== 0) return;
      const under = elementAt(e.clientX, e.clientY);
      if (!under) return;
      const el = inspectTarget(under);
      showHighlight(el);
      openPanel(el, e.clientX, e.clientY);
    }, true);
  }

  window.addEventListener('pointermove', e => {
    if (mode !== 'check' || panel || isOwnUi(e)) return;
    const under = elementAt(e.clientX, e.clientY);
    if (under && !(under.localName || '').startsWith('wa-')) showHighlight(inspectTarget(under));
  }, true);

  window.addEventListener('keydown', e => {
    if (mode === 'check' && e.key === 'Escape') {
      e.stopImmediatePropagation();
      if (panel) closePanel();
      else changeMode('record');
    }
  }, true);

  function applyMode(m) {
    mode = m === 'check' ? 'check' : 'record';
    if (mode !== 'check') {
      closePanel();
      hideHighlight();
    }
    updateBadge();
  }

  function changeMode(m) {
    applyMode(m);
    try {
      window.__waModeChanged(mode);
    } catch (e) {
      // Bağlantı yoksa mod yalnızca bu sayfada değişmiş olur.
    }
  }

  window.__waSetMode = applyMode;

  // Sayfa geçişinde betik yeniden çalışır; geçerli modu uygulamadan öğrenir.
  try {
    Promise.resolve(window.__waGetMode()).then(applyMode, () => {});
  } catch (e) {
    // Bağlantı henüz hazır değil: varsayılan kayıt modu.
  }

  // Tarayıcıdan ayrılmadan mod değiştirmek için küçük çubuk. Özel etiket adı kullanılır ki
  // sitedeki div'lerin :nth-of-type sırası bozulmasın.
  let badgeRecord = null;
  let badgeCheck = null;

  function updateBadge() {
    if (!badgeRecord) return;
    badgeRecord.className = mode === 'record' ? 'on rec' : '';
    badgeCheck.className = mode === 'check' ? 'on chk' : '';
  }

  function installBadge() {
    if (window.top !== window || document.querySelector('wa-recording-badge')) return;
    const badge = document.createElement('wa-recording-badge');
    badge.setAttribute('style', 'position:fixed;right:12px;bottom:12px;z-index:2147483647');
    const root = badge.attachShadow({ mode: 'open' });
    const style = document.createElement('style');
    style.textContent = [
      '.bar{display:flex;gap:2px;background:#333;padding:3px;border-radius:16px;box-shadow:0 2px 6px rgba(0,0,0,.3);opacity:.92}',
      'button{all:unset;cursor:pointer;color:#ddd;font:600 12px system-ui,sans-serif;padding:5px 10px;border-radius:13px}',
      'button.on.rec{background:#d32f2f;color:#fff}',
      'button.on.chk{background:#f9a825;color:#222}',
    ].join('');
    const bar = document.createElement('div');
    bar.className = 'bar';
    badgeRecord = document.createElement('button');
    badgeRecord.textContent = '● KAYIT';
    badgeRecord.dataset.mode = 'record';
    badgeRecord.addEventListener('click', () => changeMode('record'));
    badgeCheck = document.createElement('button');
    badgeCheck.textContent = '◎ KONTROL';
    badgeCheck.dataset.mode = 'check';
    badgeCheck.addEventListener('click', () => changeMode('check'));
    bar.append(badgeRecord, badgeCheck);
    root.append(style, bar);
    (document.body || document.documentElement).appendChild(badge);
    updateBadge();
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', installBadge);
  else installBadge();
})();
