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

// Step 4 -- Switch to Read confirmation.
// Manage is the default permission for newly granted sites because it lets the
// customer enable libraries from this app. Switching to Read restricts the app
// to read-only operations, which means the customer can no longer enable
// libraries (or make other admin-grade changes) until they switch back. We warn
// once, on submit; the customer can still choose Read if they want -- by design,
// not least-privilege-by-default, per UX direction.
function confirmSwitchToRead() {
    return confirm(
        'Switching to Read means this app will no longer be able to enable libraries ' +
        'or make administrative changes on this site. You can switch back to Manage ' +
        'whenever you need to make admin changes.\n\nContinue?'
    );
}
