// Small helpers for the verifier page, called through IJSRuntime.
window.obcVerify = {
    // Updates the address bar without a reload or a new history entry, keeping Blazor's history state.
    replaceUrl: function (url) {
        history.replaceState(history.state, "", url);
    },
    copyText: function (text) {
        return navigator.clipboard.writeText(text);
    }
};
