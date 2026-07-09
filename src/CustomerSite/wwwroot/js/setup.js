// Read and Understood post-acceptance setup page client behaviour.
// Polls /Setup/{id}/Status.json while a step shows "in-flight" so the UI
// flips silently from "Propagating..." to "Propagated" without a refresh.

(function () {
    var checklist = document.querySelector('.setup-checklist');
    if (!checklist) return;

    var subscriptionId = checklist.getAttribute('data-subscription-id');
    if (!subscriptionId) return;

    var poll = checklist.querySelector('.setup-step__hint--inflight');
    if (!poll) return;

    var slowHint = poll.querySelector('.setup-step__hint--slow');
    var stuckHint = null;
    var startedAt = Date.now();

    function tick() {
        fetch('/Setup/' + subscriptionId + '/Status.json', { credentials: 'same-origin' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (s) {
                if (!s) {
                    schedule();
                    return;
                }
                if (s.regionFanOutComplete) {
                    location.reload();
                    return;
                }
                var elapsed = Date.now() - startedAt;
                if (slowHint && elapsed > 120000) {
                    slowHint.style.display = 'block';
                }
                if (elapsed > 600000 && !stuckHint) {
                    stuckHint = document.createElement('p');
                    stuckHint.className = 'setup-step__hint setup-step__hint--warn';
                    stuckHint.textContent = 'Propagation is taking longer than expected. Contact support if this persists.';
                    poll.parentNode.appendChild(stuckHint);
                }
                schedule();
            })
            .catch(function () { schedule(); });
    }

    function schedule() {
        setTimeout(tick, 5000);
    }

    schedule();
})();

// ---------------------------------------------------------------------------
// Fluent-styled confirmation dialog.
// The setup page has no Fluent UI (React) runtime -- it's server-rendered MVC --
// so this is a lightweight modal that echoes the Azure/Fluent look already used
// across setup.css (Segoe UI, #0078d4, 2px corners, overlay scrim). Replaces the
// native window.confirm() prompts. Returns a Promise<boolean>.
function setupConfirm(opts) {
    return new Promise(function (resolve) {
        var lastFocused = document.activeElement;

        var overlay = document.createElement('div');
        overlay.className = 'setup-dialog-overlay';

        var dialog = document.createElement('div');
        dialog.className = 'setup-dialog';
        dialog.setAttribute('role', 'alertdialog');
        dialog.setAttribute('aria-modal', 'true');
        dialog.setAttribute('aria-labelledby', 'setup-dialog-title');

        var title = document.createElement('h2');
        title.className = 'setup-dialog__title';
        title.id = 'setup-dialog-title';
        title.textContent = opts.title;

        var body = document.createElement('p');
        body.className = 'setup-dialog__body';
        body.textContent = opts.message;

        var footer = document.createElement('div');
        footer.className = 'setup-dialog__footer';

        var cancelBtn = document.createElement('button');
        cancelBtn.type = 'button';
        cancelBtn.className = 'setup-dialog__btn setup-dialog__btn--secondary';
        cancelBtn.textContent = opts.cancelText || 'Cancel';

        var confirmBtn = document.createElement('button');
        confirmBtn.type = 'button';
        confirmBtn.className = 'setup-dialog__btn ' +
            (opts.destructive ? 'setup-dialog__btn--danger' : 'setup-dialog__btn--primary');
        confirmBtn.textContent = opts.confirmText || 'OK';

        footer.appendChild(cancelBtn);
        footer.appendChild(confirmBtn);
        dialog.appendChild(title);
        dialog.appendChild(body);
        dialog.appendChild(footer);
        overlay.appendChild(dialog);
        document.body.appendChild(overlay);

        confirmBtn.focus();

        function close(result) {
            document.removeEventListener('keydown', onKey);
            if (overlay.parentNode) overlay.parentNode.removeChild(overlay);
            if (lastFocused && lastFocused.focus) lastFocused.focus();
            resolve(result);
        }

        function onKey(e) {
            if (e.key === 'Escape') { close(false); }
        }

        cancelBtn.addEventListener('click', function () { close(false); });
        confirmBtn.addEventListener('click', function () { close(true); });
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) { close(false); }
        });
        document.addEventListener('keydown', onKey);
    });
}

// Wording for each confirmed action. Keyed by the form's data-confirm value.
//  - switch-read: Manage is the default for newly granted sites (lets the customer
//    enable libraries). Read is read-only, so enable-library and other admin-grade
//    changes stop until they switch back. Warn, but allow -- by UX direction.
//  - remove-site: revokes the per-site grant and drops the enrollment. Destructive
//    and not easily undone (re-add + re-grant), so the primary button is danger-styled.
function setupConfirmOptions(form) {
    var kind = form.getAttribute('data-confirm');
    if (kind === 'switch-read') {
        return {
            title: 'Switch to Read?',
            message: 'Read means this app will no longer be able to enable libraries or ' +
                'make administrative changes on this site. You can switch back to Manage ' +
                'whenever you need to make admin changes.',
            confirmText: 'Switch to Read',
            destructive: false
        };
    }
    if (kind === 'remove-site') {
        var url = form.getAttribute('data-site-url') || 'this site';
        return {
            title: 'Remove site?',
            message: 'Read and Understood\'s permission on "' + url + '" will be revoked and ' +
                'the site will be removed from setup. Acknowledgement tracking on this site ' +
                'will stop. This cannot be undone.',
            confirmText: 'Remove site',
            destructive: true
        };
    }
    return null;
}

// Intercept submits of any [data-confirm] form, gate them behind the Fluent dialog,
// and re-submit on confirm. form.submit() bypasses this listener (it fires no submit
// event), so there is no recursion.
document.addEventListener('submit', function (e) {
    var form = e.target;
    if (!form || !form.getAttribute || !form.hasAttribute('data-confirm')) return;

    var opts = setupConfirmOptions(form);
    if (!opts) return;

    e.preventDefault();
    setupConfirm(opts).then(function (ok) {
        if (ok) { form.submit(); }
    });
});
