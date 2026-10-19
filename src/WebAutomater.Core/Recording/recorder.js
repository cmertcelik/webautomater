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
    const t = eventTarget(e);
    if (!t) return;
    const el = clickTarget(t);
    if (shouldRecordClick(e, el)) send({ type: 'click', ...describe(el) });
  }, true);

  window.addEventListener('dblclick', e => {
    const t = eventTarget(e);
    if (!t || !e.isTrusted) return;
    const el = clickTarget(t);
    if (shouldRecordClick(e, el)) send({ type: 'dblclick', ...describe(el) });
  }, true);

  window.addEventListener('input', e => {
    const t = eventTarget(e);
    if (!t || !isTextInput(t)) return;
    const isNative = t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement;
    send({ type: 'fill', value: isNative ? t.value : t.innerText, ...describe(t) });
  }, true);

  window.addEventListener('change', e => {
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

  // Kayıt sırasında kullanıcıya görsel geri bildirim. Özel etiket adı kullanılır ki
  // sitedeki div'lerin :nth-of-type sırası bozulmasın; tıklamaları engellemez.
  function installBadge() {
    if (window.top !== window || document.querySelector('wa-recording-badge')) return;
    const badge = document.createElement('wa-recording-badge');
    badge.textContent = '● KAYIT';
    badge.setAttribute('style', [
      'position:fixed', 'right:12px', 'bottom:12px', 'z-index:2147483647', 'pointer-events:none',
      'background:#d32f2f', 'color:#fff', 'font:600 12px system-ui,sans-serif', 'padding:6px 10px',
      'border-radius:14px', 'opacity:.85', 'box-shadow:0 2px 6px rgba(0,0,0,.3)',
    ].join(';'));
    (document.body || document.documentElement).appendChild(badge);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', installBadge);
  else installBadge();
})();
