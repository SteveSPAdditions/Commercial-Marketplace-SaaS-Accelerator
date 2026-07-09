// Inline setup accordion for the subscriptions list.
//
// Each subscription row has a hidden detail row holding an empty .setup-inline-panel.
// The panel is lazy-loaded (GET {data-panel-url}) on first expand, and its step forms
// are submitted via fetch so the panel updates in place -- reusing the same partial and
// endpoints as the standalone /Setup page. OAuth consent steps (Step 3/5) are plain
// links that navigate out and return via ?expand={id}; interactive-consent challenges on
// site actions come back as { requiresRedirect } and we navigate to them as a full page.
//
// Relies on setup.js (loaded first) for the shared window.setupConfirm dialog and
// window.setupConfirmOptions wording.
(function () {
    'use strict';

    var AJAX_HEADERS = { 'X-Requested-With': 'XMLHttpRequest' };
    var pollTimers = new WeakMap();

    function panelOf(detailRow) {
        return detailRow ? detailRow.querySelector('.setup-inline-panel') : null;
    }

    function contentOf(panel) {
        return panel ? panel.querySelector('.setup-inline-panel__content') : null;
    }

    function toggleFor(detailRow) {
        return document.querySelector('.setup-expand-toggle[data-target="' + detailRow.id + '"]');
    }

    function isCollapsed(panel) {
        var row = panel.closest('.setup-detail-row');
        return !row || row.hidden;
    }

    // ---- Lazy load ----------------------------------------------------------

    function loadPanel(panel) {
        var content = contentOf(panel);
        var url = panel.getAttribute('data-panel-url');
        if (!content || !url) return;

        fetch(url, { credentials: 'same-origin', headers: AJAX_HEADERS })
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.text();
            })
            .then(function (html) {
                content.innerHTML = html;
                panel.setAttribute('data-loaded', 'true');
                schedulePolling(panel);
            })
            .catch(function () {
                content.innerHTML =
                    '<p class="setup-inline-panel__loading">Couldn\'t load setup. ' +
                    '<a href="#" class="setup-inline-retry">Retry</a></p>';
            });
    }

    function ensureLoaded(panel) {
        if (panel && panel.getAttribute('data-loaded') !== 'true') {
            loadPanel(panel);
        }
    }

    // ---- Expand / collapse --------------------------------------------------

    function expand(detailRow) {
        detailRow.hidden = false;
        var toggle = toggleFor(detailRow);
        if (toggle) toggle.setAttribute('aria-expanded', 'true');
        ensureLoaded(panelOf(detailRow));
    }

    function collapse(detailRow) {
        detailRow.hidden = true;
        var toggle = toggleFor(detailRow);
        if (toggle) toggle.setAttribute('aria-expanded', 'false');
        stopPolling(panelOf(detailRow));
    }

    document.addEventListener('click', function (e) {
        var trigger = e.target.closest('.setup-expand-toggle, .setup-expand-link');
        if (!trigger) return;
        var targetId = trigger.getAttribute('data-target');
        var detailRow = targetId && document.getElementById(targetId);
        if (!detailRow) return; // no panel (e.g. not subscribed) -> let the link behave normally
        e.preventDefault();
        if (detailRow.hidden) { expand(detailRow); } else { collapse(detailRow); }
    });

    // Retry link inside a failed panel.
    document.addEventListener('click', function (e) {
        var retry = e.target.closest('.setup-inline-retry');
        if (!retry) return;
        e.preventDefault();
        var panel = retry.closest('.setup-inline-panel');
        if (panel) loadPanel(panel);
    });

    // ---- AJAX form submission (region + site actions) -----------------------

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form || !form.closest || !form.closest('.setup-inline-panel')) return;
        e.preventDefault();

        var opts = form.hasAttribute('data-confirm') && typeof window.setupConfirmOptions === 'function'
            ? window.setupConfirmOptions(form)
            : null;
        var gate = (opts && typeof window.setupConfirm === 'function')
            ? window.setupConfirm(opts)
            : Promise.resolve(true);

        gate.then(function (ok) { if (ok) { postForm(form); } });
    });

    function postForm(form) {
        var panel = form.closest('.setup-inline-panel');
        var content = contentOf(panel);
        if (!content) return;

        stopPolling(panel);
        panel.classList.add('is-busy');

        fetch(form.action, {
            method: (form.getAttribute('method') || 'post').toUpperCase(),
            credentials: 'same-origin',
            headers: AJAX_HEADERS,
            body: new FormData(form)
        })
            .then(function (r) {
                var ctype = r.headers.get('Content-Type') || '';
                if (ctype.indexOf('application/json') !== -1) {
                    return r.json().then(function (j) {
                        // Interactive-consent challenge / auth bounce: navigate as a full page.
                        if (j && j.requiresRedirect && j.url) { window.location.href = j.url; }
                    });
                }
                return r.text().then(function (html) {
                    content.innerHTML = html;
                    panel.setAttribute('data-loaded', 'true');
                    schedulePolling(panel);
                });
            })
            .catch(function () {
                content.insertAdjacentHTML('afterbegin',
                    '<div class="alert alert-danger setup-flash text-left" role="alert">' +
                    'Something went wrong. Please try again.</div>');
            })
            .then(function () { panel.classList.remove('is-busy'); });
    }

    // ---- Scoped polling while a step is propagating -------------------------
    // Replaces the standalone page's full-page reload: re-fetch just this panel while
    // it shows an in-flight hint (e.g. region propagation), and stop once it clears.

    function schedulePolling(panel) {
        stopPolling(panel);
        if (isCollapsed(panel)) return;
        var content = contentOf(panel);
        if (!content || !content.querySelector('.setup-step__hint--inflight')) return;

        var url = panel.getAttribute('data-panel-url');
        var timer = setTimeout(function () {
            fetch(url, { credentials: 'same-origin', headers: AJAX_HEADERS })
                .then(function (r) { return r.ok ? r.text() : null; })
                .then(function (html) {
                    // Don't clobber a field the user is actively editing.
                    if (html != null && !isCollapsed(panel) && !panel.contains(document.activeElement)) {
                        content.innerHTML = html;
                    }
                    schedulePolling(panel);
                })
                .catch(function () { schedulePolling(panel); });
        }, 5000);
        pollTimers.set(panel, timer);
    }

    function stopPolling(panel) {
        if (!panel) return;
        var t = pollTimers.get(panel);
        if (t) { clearTimeout(t); pollTimers.delete(panel); }
    }

    // ---- Auto-open a server-pre-expanded row (e.g. ?expand= after OAuth) -----

    document.querySelectorAll('.setup-detail-row:not([hidden])').forEach(function (row) {
        var toggle = toggleFor(row);
        if (toggle) toggle.setAttribute('aria-expanded', 'true');
        ensureLoaded(panelOf(row));
    });
})();
