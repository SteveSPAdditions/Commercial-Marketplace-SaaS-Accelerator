// Pages that have #myModal (Subscriptions, etc.) warm up the modal here.
// Pages without it (Error/OOPS, Setup, Privacy, ...) skip silently — without this
// guard, Bootstrap throws "Cannot read properties of undefined (reading 'backdrop')"
// because the selector resolves to null.
(function () {
    var modalElement = document.getElementById('myModal');
    if (!modalElement || typeof bootstrap === 'undefined' || !bootstrap.Modal) {
        return;
    }
    var myModal = new bootstrap.Modal(modalElement);
    setTimeout(function () { myModal.hide(); }, 1000);
})();